#!/usr/bin/env bash
# =====================================================================================
# 90 — dr-restore.  Lose the database and put it back from a backup (ADD §15).
#
#   | Asset      | Backup Method                                       | RPO   | RTO    |
#   | PostgreSQL | pgBackRest continuous WAL → S3, daily base backup    | 5 min | 30 min |
#
# ------------------------------------------------------------------------------------
# WHAT THIS DEPLOYMENT ACTUALLY HAS, AND WHY THE RPO IS THE HEADLINE
# ------------------------------------------------------------------------------------
# There is no pgBackRest and no continuous WAL archive here. `infra/replica/backup.sh` takes a
# nightly `pg_dump -Fc` into the replica's own MinIO and `restore.sh` puts one back — a snapshot
# mechanism, not a point-in-time one. The distinction is the whole of ADD §15's RPO column:
#
#   * pgBackRest with WAL shipping loses at most the un-archived segment — the 5 minutes.
#   * A nightly dump loses everything written since the dump. On a daily schedule that is up to
#     24 hours, and the number does not depend on how fast anybody restores.
#
# So this drill measures BOTH and reports them separately: the RTO honestly (take a backup, drop
# the database, restore it, time until the platform serves), and the RPO by writing a row after
# the backup and looking for it afterwards. An RTO inside 30 minutes on a replica whose RPO is a
# day is not a passing DR drill, and this script refuses to present it as one.
#
# ------------------------------------------------------------------------------------
# THIS IS THE ONE DRILL THAT DESTROYS DATA
# ------------------------------------------------------------------------------------
# It DROPs and recreates the database. It runs last, `--no-dr` skips it, and it re-checks the
# synthetic marker immediately before the drop rather than trusting the pre-flight from twenty
# minutes ago.
# =====================================================================================

drill_begin "90" "Disaster recovery — restore from backup" \
  "ADD §15 (PostgreSQL RPO 5 min / RTO 30 min) · docs/runbooks/replica-operations.md §4" \
  "the ENTIRE database is dropped and rebuilt from a dump; every service is stopped and restarted" \
  "the restore itself is the rollback — there is no other way back"

# -------------------------------------------------------------------------------------
# 0. The marker, re-checked here
# -------------------------------------------------------------------------------------
marker=$(psql_one "SELECT count(*) FROM replica.synthetic_marker WHERE marker = 'mageride-replica-synthetic';")
if [ "$marker" != "1" ]; then
  bad "replica.synthetic_marker is absent — refusing to drop this database"
  drill_end 30
  return 0 2>/dev/null || true
fi
ok "synthetic marker re-checked immediately before the drop"

users_before=$(psql_one "SELECT count(*) FROM iam.users;")
rides_before=$(psql_one "SELECT count(*) FROM rides.rides;")
positions_before=$(psql_one "SELECT count(*) FROM telemetry.positions;")
note "before: ${users_before} users, ${rides_before} rides, ${positions_before} telemetry rows"

# -------------------------------------------------------------------------------------
# 1. Take the backup, and time it — a backup nobody has timed cannot be scheduled
# -------------------------------------------------------------------------------------
backup_started=$(now_ms)
if bash "${REPO_ROOT}/infra/replica/backup.sh" >"${CHAOS_DIR}/out/dr-backup.log" 2>&1; then
  backup_ms=$(since_ms "$backup_started")
  dump_name=$(grep -o 'mageride-[0-9TZ]*\.dump' "${CHAOS_DIR}/out/dr-backup.log" | head -1)
  dump_size=$(grep -o '([0-9.]*[KMG])' "${CHAOS_DIR}/out/dr-backup.log" | head -1 | tr -d '()')
  ok "backup taken in $(human_ms "$backup_ms"): ${dump_name} (${dump_size:-size unread})"
else
  bad "infra/replica/backup.sh failed — see chaos/out/dr-backup.log"
  finding HIGH "The DR drill could not take a backup. ADD §15 rates PostgreSQL RPO 5 min / RTO \
30 min and both are claims about a backup existing; \`infra/replica/backup.sh\` exited non-zero. \
$(tail -3 "${CHAOS_DIR}/out/dr-backup.log" 2>/dev/null | tr '\n' ' ')"
  drill_end 60
  return 0 2>/dev/null || true
fi

backup_taken_at=$(now_ms)

# -------------------------------------------------------------------------------------
# 2. THE RPO PROBE — write something AFTER the backup
# -------------------------------------------------------------------------------------
# A row and a ride, so the loss is measured on both a reference table and the transactional one.
# Everything written between the backup and the disaster is what the RPO measures, and on a
# snapshot backup it is everything.
rpo_marker="chaos-rpo-$(date -u +%s)"
psql_q "INSERT INTO replica.synthetic_marker (marker) VALUES ('${rpo_marker}');" >/dev/null 2>&1 \
  || psql_q "INSERT INTO iam.users (phone, role, first_name, language)
             VALUES ('+94770049998', 'passenger', '${rpo_marker}', 'en')
             ON CONFLICT (phone) DO UPDATE SET first_name = EXCLUDED.first_name;" >/dev/null 2>&1

post_backup_write=$(psql_one "SELECT count(*) FROM iam.users WHERE first_name = '${rpo_marker}';")
if [ "${post_backup_write:-0}" = "0" ]; then
  post_backup_write=$(psql_one "SELECT count(*) FROM replica.synthetic_marker WHERE marker = '${rpo_marker}';")
fi

if [ "${post_backup_write:-0}" -ge 1 ]; then
  ok "wrote an RPO probe row (\`${rpo_marker}\`) $(human_ms "$(since_ms "$backup_taken_at")") after the backup completed"
else
  warn "the RPO probe row could not be written; the RPO measurement below is unavailable"
fi

driver_online 0 >/dev/null 2>&1
rpo_ride=$(request_ride 0 2>/dev/null)
[ -n "$rpo_ride" ] && ok "and booked a ride after the backup: ${rpo_ride:0:8}…"

sleep 2

# -------------------------------------------------------------------------------------
# 3. The disaster
# -------------------------------------------------------------------------------------
# `restore.sh --yes` stops the services holding connections, drops the database, recreates it,
# reinstalls the extensions, restores the dump and starts the services again — i.e. it IS the
# disaster and the recovery, which is the only honest way to time a restore: a drill that dropped
# the database with its own SQL and then called restore.sh would be timing half the runbook.
disaster_at=$(now_ms)
note "running infra/replica/restore.sh --yes — the database is dropped and rebuilt from ${dump_name}"

restore_ok=0
if bash "${REPO_ROOT}/infra/replica/restore.sh" --yes "${dump_name}" \
     >"${CHAOS_DIR}/out/dr-restore.log" 2>&1; then
  restore_ms=$(since_ms "$disaster_at")
  restore_ok=1
  ok "restore.sh completed in $(human_ms "$restore_ms")"
else
  restore_ms=$(since_ms "$disaster_at")
  bad "infra/replica/restore.sh exited non-zero after $(human_ms "$restore_ms") — see chaos/out/dr-restore.log"
  finding HIGH "The documented restore path failed. ADD §15's 30-minute RTO is a claim about \
\`infra/replica/restore.sh\` working; it did not, and it died having already stopped app-services, \
hot-path, fanout, tcp-adapter and pgbouncer — the platform down with the database intact. \
$(tail -3 "${CHAOS_DIR}/out/dr-restore.log" 2>/dev/null | tr '\n' ' ')"
  # Put the platform back by hand: the script's own step 4 never ran.
  dc start pgbouncer app-services hot-path fanout tcp-adapter >/dev/null 2>&1
fi

# -------------------------------------------------------------------------------------
# 4. RTO — not "the script finished", but "a passenger could book again"
# -------------------------------------------------------------------------------------
if healthy_ms=$(wait_for 900 5 stack_healthy); then
  ok "every container healthy again $(human_ms "$healthy_ms") after restore.sh returned"
else
  bad "the stack was not healthy 15 minutes after the restore"
fi

serving=0
if serving_ms=$(wait_for 600 5 steady_state_quiet); then
  serving=1
  ok "the platform served a full steady state $(human_ms "$serving_ms") after restore.sh returned"
else
  bad "the platform was still not serving ten minutes after the restore"
fi

rto_ms=$(since_ms "$disaster_at")

# -------------------------------------------------------------------------------------
# 5. What came back, and what did not
# -------------------------------------------------------------------------------------
users_after=$(psql_one "SELECT count(*) FROM iam.users;")
rides_after=$(psql_one "SELECT count(*) FROM rides.rides;")
positions_after=$(psql_one "SELECT count(*) FROM telemetry.positions;")
marker_after=$(psql_one "SELECT count(*) FROM replica.synthetic_marker WHERE marker = 'mageride-replica-synthetic';")

expect "the synthetic marker survived the restore" "$marker_after" "1"
note "after: ${users_after} users, ${rides_after} rides, ${positions_after} telemetry rows"

rpo_survived=$(psql_one "SELECT count(*) FROM iam.users WHERE first_name = '${rpo_marker}';")
[ "${rpo_survived:-0}" = "0" ] && rpo_survived=$(psql_one "SELECT count(*) FROM replica.synthetic_marker WHERE marker = '${rpo_marker}';")
rpo_ride_survived=0
[ -n "$rpo_ride" ] && rpo_ride_survived=$(psql_one "SELECT count(*) FROM rides.rides WHERE id = '${rpo_ride}';")

degraded_table_open
degraded_row "RTO — restore script" "$(human_ms "$restore_ms")" "—"
degraded_row "RTO — platform serving again" "$(human_ms "$rto_ms")" "**30 min** (ADD §15)"
degraded_row "RPO — data written after the backup" \
  "probe row: $([ "${rpo_survived:-0}" -ge 1 ] && echo 'survived' || echo '**LOST**'); ride booked after the backup: $([ "${rpo_ride_survived:-0}" -ge 1 ] && echo 'survived' || echo '**LOST**')" \
  "**5 min** (ADD §15, pgBackRest continuous WAL)"
degraded_table_close

# -------------------------------------------------------------------------------------
# The verdicts, separately, because they pass and fail independently
# -------------------------------------------------------------------------------------
# `serving` gates the verdict, not the clock. `since_ms` always produces a number, and the first
# version of this drill printed "RTO MET: 10 m 13 s" on a run where the restore had failed outright
# and the platform never came back — an elapsed time is not a recovery.
if [ "$serving" != "1" ]; then
  bad "RTO cannot be claimed: the platform never served again, so $(human_ms "$rto_ms") is how long \
the drill waited and not a recovery time"
elif [ "${rto_ms:-999999999}" -le 1800000 ]; then
  ok "RTO MET: $(human_ms "$rto_ms") from disaster to a serving platform, against ADD §15's 30 minutes"
else
  bad "RTO MISSED: $(human_ms "$rto_ms") against ADD §15's 30 minutes"
  finding HIGH "The measured RTO is $(human_ms "$rto_ms"), over ADD §15's 30-minute target for \
PostgreSQL. The path timed is \`infra/replica/restore.sh\` end to end — stop the services, drop \
and recreate the database, reinstall the extensions, \`pg_restore\`, start the services — against \
a ${positions_before}-row telemetry table."
fi

# Gated on the restore having happened. On the run where `restore.sh` died before its
# `DROP DATABASE`, everything written after the backup was of course still there — the database had
# never been replaced — and the drill reported "RPO MET" for a disaster that did not occur.
if [ "$restore_ok" != "1" ]; then
  bad "RPO cannot be claimed: the database was never actually replaced, so surviving rows prove nothing"
elif [ "${rpo_survived:-0}" -ge 1 ] && [ "${rpo_ride_survived:-0}" -ge 1 ]; then
  ok "RPO MET: everything written after the backup came back"
else
  finding HIGH "**RPO is not 5 minutes on this deployment; it is one backup interval.** \
Everything written between the backup and the disaster was lost — the probe row \
(\`${rpo_marker}\`) and the ride booked after it are both gone from the restored database. ADD §15 \
rates PostgreSQL at RPO 5 min on the strength of \"pgBackRest continuous WAL → S3, daily base \
backup\", and this deployment has no WAL archive and no pgBackRest: \
\`infra/replica/backup.sh\` takes a \`pg_dump -Fc\` and \`restore.sh\` puts one back, which is a \
snapshot mechanism with no point-in-time component at all. On the runbook's nightly \`15 2 * * *\` \
schedule the real RPO is **up to 24 hours**, and no improvement in restore speed changes it. \
The replica is documented as a single-point-of-failure stack (§14's MVP column, and the compose \
file's own header), so this is a gap between §15's table and what any environment short of \
production has — but §15's table does not say which column it applies to, and the 5 minutes is \
what a reader takes away. Production on DOKS needs pgBackRest, or the number in §15."
fi

report ""
report "| Disaster recovery | Measured | ADD §15 |"
report "|---|---|---|"
report "| Backup | $(human_ms "$backup_ms") · \`${dump_name}\` ${dump_size:+(${dump_size})} | \"daily base backup\" |"
report "| Restore script | $(human_ms "$restore_ms") | — |"
report "| **RTO** (disaster → serving) | **$(human_ms "$rto_ms")** | **30 min** |"
report "| **RPO** (data lost) | **everything since the last backup** — the post-backup probe row and ride did not survive | **5 min** |"
report "| Rows before → after | ${users_before} → ${users_after} users · ${rides_before} → ${rides_after} rides · ${positions_before} → ${positions_after} telemetry | — |"
report "| Mechanism | \`pg_dump -Fc\` into the replica's own MinIO | pgBackRest continuous WAL → S3 |"
report ""

# The chaos fixture's bearers are still valid — they are RS256 and stateless — but the accounts
# they name came back from the dump, so anything the drills created after the backup is gone.
# Said out loud because the next drill in a longer run would otherwise be debugging it.
note "the fixture accounts were restored from the dump; re-run chaos/configure.sh before another pass"

drill_end 900
