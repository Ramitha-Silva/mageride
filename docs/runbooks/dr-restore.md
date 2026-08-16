# Disaster recovery — restoring Postgres from the Wasabi repository

Alerts: `PostgresWalArchiveFailing`, `PostgresWalArchiveStalled`, `PgDumpJobFailing` ·
Rehearsal: `bash infra/scripts/dr-rehearsal.sh` · ADD §15 (RPO 5 min / RTO 30 min), D7' §8

## First action

**Decide whether you need a restore at all.** In almost every case you do not, and a restore is
the slowest and most destructive option on the table.

```bash
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml list
```

If ANY member is `running` or `streaming`, this is a failover, not a disaster —
`docs/runbooks/postgres-failover.md`. A restore rolls the whole platform back to a point in the
past and discards everything committed since. Use it for exactly two situations:

1. every member of the cluster is gone, or the volumes are unrecoverable;
2. something logically destructive was committed at a known time (a bad migration, a mistaken
   bulk update) and the platform has to go back to just before it.

## 1. What is in the repository, and how far back

```bash
kubectl -n mageride exec postgres-0 -c postgres -- pgbackrest --stanza=mageride info
```

Read three things: the newest full backup's timestamp, the WAL archive range, and whether the
archive is CONTINUOUS across the point you want. A gap in the WAL means the recoverable window
ends at the gap, whatever the backups say.

Retention is `repo1-retention-full=2` — two full backups and the WAL to recover from the older
of them, which is about 48 hours of point-in-time recovery. Older than that, the only copy is the
nightly `pg_dump` in `s3://mageride-production-pgdump/`, which is not point-in-time.

## 2. The RPO, and what makes it true

`archive_timeout = 60` in `patroni.yml` forces a WAL segment switch every minute even when the
platform is idle, so the worst case between the last archived segment and a lost primary is **one
minute of writes** — against ADD §15's five.

That number depends entirely on archiving having been healthy. `PostgresWalArchiveStalled` and
`PostgresWalArchiveFailing` are the two alerts that say it was not; if either has been firing,
the real RPO is the age of the last successful push and `pgbackrest info` is what says when that
was.

`archive-push-queue-max` is deliberately unset (`pgbackrest.conf` explains). A persistently
failing archive therefore fills `pg_wal` and eventually stops the database — loudly — instead of
silently dropping segments and leaving a repository that cannot recover anything.

## 3. Restoring, when the cluster is gone

Patroni must not be running while the data directory is replaced.

```bash
# 1. stop Patroni from fighting the restore
kubectl -n mageride scale statefulset postgres --replicas=0
kubectl -n mageride wait --for=delete pod -l app=postgres --timeout=300s

# 2. clear the DCS. Without this, the first pod back believes it is joining a cluster whose
#    leader is a pod that no longer exists, and it will loop trying to clone from it.
kubectl -n mageride delete configmap mageride-pg-leader mageride-pg-config \
        mageride-pg-failover mageride-pg-sync --ignore-not-found

# 3. bring ONE member back, with Patroni paused so it does not initialise anything
kubectl -n mageride scale statefulset postgres --replicas=1
kubectl -n mageride wait --for=condition=Ready pod/postgres-0 --timeout=600s
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml pause mageride-pg

# 4. restore into it
kubectl -n mageride exec postgres-0 -c postgres -- bash -lc '
  pg_ctl -D /home/postgres/pgdata/data -m fast -w stop
  rm -rf /home/postgres/pgdata/data/*
  pgbackrest --stanza=mageride --type=time \
    --target="2026-08-13 11:57:51+00" --target-action=promote restore
  pg_ctl -D /home/postgres/pgdata/data -w start'

# 5. confirm the recovery target landed where you meant it to, THEN resume
kubectl -n mageride exec postgres-0 -c postgres -- \
  psql -d mageride -c "select pg_is_in_recovery(), now();"
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml resume mageride-pg

# 6. the other two members rebuild from the restored primary
kubectl -n mageride scale statefulset postgres --replicas=3
```

`--type=time --target=…` is a point in time. `--type=default` restores to the end of the archive
— everything there is — and is what you want when the loss is physical rather than logical.

**Step 5 before step 6.** Once the replicas rebuild, the timeline is committed and going further
back means starting over.

## 4. Restoring one table, not the platform

This is what the nightly `pg_dump` exists for, and it is a completely different operation: no
downtime, no rollback, nothing else affected.

```bash
# newest dump
curl --aws-sigv4 "aws:amz:ap-southeast-1:s3" --user "$KEY:$SECRET" \
  "https://mageride-production-pgdump.s3.ap-southeast-1.wasabisys.com/?list-type=2&prefix=pg_dump/"
# restore ONE table into a scratch schema and copy across deliberately
pg_restore --dbname=mageride --schema=billing --table=payouts --data-only mageride-….dump
```

Restore into a scratch schema and move the rows with SQL you have read. `pg_restore` straight
over a live table is not a merge — it appends, and it will not tell you it did.

## 5. After any restore

* **The outbox.** Rows restored to a point in the past may have already been published to
  Redpanda. Consumers are idempotent by key (E-09), so a re-publish is absorbed, but check
  `OutboxDispatchLagHigh` after resuming.
* **Redis holds newer state than Postgres.** Positions, locks and offers survive a Postgres
  restore and now describe rides the database has never heard of. Flushing the geo index is safe
  (`limitedLive` degradation, ADD §14.1); flushing the LOCKS is not — read
  `docs/runbooks/redis-sentinel-failover.md` first.
* **Take a new base backup immediately.** The restored cluster is on a new timeline and the
  repository's newest full backup is from before it.

## 6. What the RTO actually is, and why the rehearsed number is not it

`infra/scripts/dr-rehearsal.sh` runs this whole procedure against the replica's object store and
times it. Measured 2026-08-13, on a 7.7 MB / 1,264-file cluster:

```
RTO 122.2 s   =  copy 119.5 s  +  postmaster start 1.6 s  +  WAL replay & promote 1.1 s
```

**Do not extrapolate that by database size.** 29 MB in two minutes is not throughput — it is
per-file cost against the object store, about 10 files a second. Raising `--process-max` from 2
to 8 on the restore was measured at 12 % faster, so parallelism is not the lever either. A 200 GB
cluster has both more bytes and more files, and which dominates depends on the repository's
latency rather than on anything the build host can demonstrate.

**So the RTO of record is the one measured against the real Wasabi repository with a
production-sized dataset, and taking it is a go-live checklist item**
(`docs/production/go-live-checklist.md` item 9). Until that number exists, ADD §15's 30 minutes is
a target and not a measurement.

## 7. Region loss

The repository is Wasabi `ap-southeast-1`, the same region as the DOKS cluster. That is the
Phase 1–2 posture ADD §14 describes ("Region failure: manual restore from backup") and it does
not survive the loss of the region itself.

The remedy, when it is wanted, is a second repository rather than a second cluster: add
`repo2-*` in `pgbackrest.conf` pointing at another Wasabi region. pgBackRest pushes WAL to both
and `--repo=2` restores from either.

## What not to do

* **Never restore into a running Patroni cluster.** Patroni will notice the data directory
  changing underneath it and either promote something or wipe your restore.
* **Never `--target-action=pause` and forget.** The cluster comes up read-only and every write on
  the platform fails with a message about recovery, which reads like a much stranger problem than
  it is.
* **Never skip `pgbackrest info` before restoring.** A restore that starts from an incomplete
  archive fails halfway through, after the data directory is already gone.
