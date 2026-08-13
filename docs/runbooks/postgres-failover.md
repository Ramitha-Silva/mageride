# Postgres — Patroni failover, and the cluster that will not form

Alerts: `PatroniClusterHasNoLeader`, `PatroniTooFewMembers`, `PostgresSynchronousStandbyLost`,
`PatroniPendingRestart` · Topology: `infra/k8s/components/launch-topology/postgres-patroni.yaml`

## First action

```bash
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml list
```

**`-c /etc/patroni/patroni.yml` is not optional.** `patronictl` with no config looks for
`/etc/patroni.yml`, does not find it, and answers `No cluster names were provided` — which reads
like "there is no cluster" during the exact incident where that would be terrifying.

Read the `Role` and `State` columns. One `Leader`, `running` and two `Replica`, `streaming` is a
healthy cluster and the page was about something else.

## What Patroni actually promises

30 seconds. `ttl: 30` in the DCS is the leader key's lifetime, refreshed every `loop_wait: 10`,
so a primary that dies without handing back the key is replaced within 30 s — which is the same
number ADD §14.1 gives passengers ("Patroni promotes replica within 30 s"). During that window
every write fails, because `Service/postgres` selects `role: primary` and no pod carries it.

Reads from `postgres-replicas` are unaffected. Nothing on this platform uses that Service yet.

## The states, and what each one means

| `patronictl list` shows | What is true | What to do |
|---|---|---|
| one Leader, two streaming replicas | healthy | nothing |
| one Leader, one streaming, one `stopped`/`start failed` | HA is gone but the platform is up | §4 |
| no Leader, members `running` | the DCS lost the key, or every member thinks it is behind | §2 |
| no Leader, members `creating replica` | a rebuild is in progress | wait; §5 if it never ends |
| Leader with `Pending restart` | a parameter changed and has not been applied | §6 |

## 1. The platform is refusing every write

That is `PatroniClusterHasNoLeader`. Confirm the Service has no endpoint:

```bash
kubectl -n mageride get endpoints postgres          # no ADDRESSES = no primary
kubectl -n mageride get pods -l app=postgres -L role
```

If a member is `running` but nothing holds the lock, the DCS is the problem, not Postgres.
Patroni's DCS here is four ConfigMaps in this namespace:

```bash
kubectl -n mageride get configmap | grep mageride-pg
```

`mageride-pg-leader` carries the lock as an annotation. If the ConfigMaps are gone, Patroni will
NOT re-elect from what is on disk — it will decide the cluster has never been initialised. See
§7 before deleting anything.

## 2. Forcing a leader

Only when §1 shows no leader for more than two minutes and at least one member is `running`.

```bash
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml failover mageride-pg
```

It asks which member to promote. **Take the one with the highest `Replay LSN`** — that is the
one with the least data loss, and `patronictl list` prints it.

`switchover` is the planned form and takes the old primary down cleanly first; `failover` is for
when the primary is already gone. Using `switchover` on a dead primary just waits.

## 3. A failover happened. What did it cost?

`synchronous_mode: true` with one synchronous standby, so **a commit that returned success was on
two members** and a promotion of the synchronous standby loses nothing. That is why it is on:
`billing.journal_postings` is a double-entry ledger and a lost posting is an unbalanced ledger
with no error anywhere.

The exception is `PostgresSynchronousStandbyLost`. `synchronous_mode_strict: false` means that
when the last standby is gone the primary KEEPS TAKING WRITES asynchronously rather than blocking
the platform. If that alert was firing before the failover, the promotion may have lost the most
recent commits. Check what the window was:

```bash
kubectl -n mageride logs postgres-0 -c postgres | grep -E "Enabled synchronous|Disabled synchronous"
```

## 4. A member is down and the other two are fine

The platform is up and has no failover target. It is urgent but it is not an outage.

```bash
kubectl -n mageride describe pod postgres-1 | tail -20
kubectl -n mageride logs postgres-1 -c postgres --tail=50
```

Most common causes, in the order they happen:

1. **The node it was on is gone.** `requiredDuringScheduling` anti-affinity means it will not
   start until there is a node with no other Postgres member on it. `kubectl get nodes`. A
   three-node pool with a node down cannot place it, and that is the design working: two members
   on one node would make a node drain take the quorum.
2. **The PVC will not attach.** DO block storage attaches to one node; if the old node still
   holds it, the volume takes a few minutes to detach.
3. **It cannot rebuild.** §5.

## 5. A replica that will not rebuild

`create_replica_methods` is `basebackup` only, and that is deliberate — patroni.yml has the
paragraph. A replica rebuilds by streaming from the leader.

```bash
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml reinit mageride-pg postgres-1
```

**This takes its bytes from the primary.** At launch size that is minutes. As the database grows
toward the 200 Gi the production overlay provisions, a rebuild during an incident competes with
production traffic on the node that is already carrying it alone. At that size, restore from the
object store instead — the method is configured in patroni.yml and just not automatic:

```bash
# on the pod that is being rebuilt, with Patroni paused so it does not fight you
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml pause mageride-pg
kubectl -n mageride exec postgres-1 -c postgres -- \
  pgbackrest --stanza=mageride --delta restore
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml resume mageride-pg
```

Confirm the repository is healthy FIRST (`pgbackrest --stanza=mageride info`). Patroni does not
fall back from a failed method to another one — it abandons the attempt — so a restore from a
repository that is not there leaves the member in a loop, not on the next method.

## 6. `Pending restart`

A parameter that needs a restart was changed. Two ways that happens and they are not the same:

* **patroni.yml changed and the ConfigMap rolled the pods.** Then the restart already happened
  and this is stale.
* **Somebody ran `patronictl edit-config`.** That writes the `mageride-pg-config` ConfigMap and
  NOT the repository, so the cluster is now running something no file describes.
  `verify-readiness.sh` §7 compares the two and fails while they disagree. Put the change in
  `infra/k8s/components/launch-topology/patroni.yml`, let ArgoCD roll it, and the drift closes.

To apply a pending restart deliberately, one member at a time, replicas first:

```bash
kubectl -n mageride exec postgres-0 -c postgres -- \
  patronictl -c /etc/patroni/patroni.yml restart mageride-pg postgres-1
```

## 7. What not to do

* **Never `kubectl delete` the `mageride-pg-*` ConfigMaps to "reset" the cluster.** They are the
  DCS. Deleting them while the members still hold data makes every member decide the cluster
  exists but has no leader, and each will try to clone from a leader that is not there —
  observed, and it loops for ever. If the DCS really has to be rebuilt, every member must be
  stopped and `patronictl` used to reinitialise, or the data directories wiped with it.
* **Never `pg_ctl promote` a replica by hand.** Patroni will notice a second timeline and the
  member will be demoted and rewound — or, if `pg_rewind` cannot run, rebuilt from scratch.
* **Never scale the StatefulSet to 0 to "restart the database".** The PDB (`minAvailable: 2`)
  will refuse the eviction, and the half that succeeds leaves one member holding everything.
* **Do not raise `synchronous_node_count` to 2 to be safer.** With three members that means both
  standbys must acknowledge every commit, so losing ONE member blocks all writes — the opposite
  of the intent.
