# Runbook — roll a deploy back (C124)

**Not an alert runbook.** The page that brought you here is about a symptom; this is what to do if
the answer is "the release we just shipped". Every alert runbook in this directory can end here.

**Time to a rolled-back production: about four minutes** — one to decide, one for the workflow, two
for ArgoCD to reconcile the rollout.

---

## First action

```bash
gh workflow run rollback.yml \
  -f environment=production \
  -f reason="<what you saw — one line, it goes in the commit message>"
```

With no `-f tag=`, it returns to **the previous distinct tag in that environment's git history**,
which is the version that was serving before the deploy you are undoing. Nothing else needs deciding
first, and the workflow refuses anything that is not a `sha-xxxxxxx` tag.

Then watch it land:

```bash
gh run watch                                  # the workflow
argocd app wait mageride-production --health  # the cluster, if you have the CLI
kubectl -n mageride rollout status deploy/api-gateway --timeout=5m
```

---

## Why it is a commit and not `kubectl rollout undo`

`kubectl rollout undo` changes the cluster and not the repository, and every Application in this
platform has `selfHeal: true`. **ArgoCD will put the bad version straight back, within three
minutes, at the worst possible moment.** The same is true of `kubectl set image` and of editing a
Deployment by hand.

The rollback workflow writes the previous tag into `infra/k8s/overlays/<env>/images/`, commits, and
lets ArgoCD reconcile. That is the same mechanism every deploy uses, which is the point: the path
you need in an incident is the path that was exercised an hour ago.

---

## What it does and does not roll back

| | |
|---|---|
| Container images (all 34, one tag) | **yes** |
| ConfigMaps, the Ingress, replica counts, HPAs | no — those are today's `main`, not the old release's |
| **The database schema** | **no. See §4.** |
| Vault secrets | no |

The manifests move forward; the images move back. That is deliberate — the alternative is reverting
the merge, which drags along every unrelated change that landed since.

If the problem is a manifest rather than an image (a bad resource limit, a wrong hostname), a
rollback will not fix it. Revert that commit instead:

```bash
git revert <the commit that changed the manifest> && git push
```

---

## 4. The schema is not rolled back, and usually does not need to be

The migration gate (`infra/scripts/migration-gate.sh`, run before every promotion) refuses any
migration that is not backward-compatible with the version that is serving. That rule exists exactly
so this moment works: the previous image can read the current schema, because it was never allowed to
stop being able to.

So in the normal case there is nothing to do. The schema is one release ahead of the code, which is
the state expand/contract is designed around.

**If the release you are rolling back contained a migration marked
`-- mageride:expand-contract phase=contract`,** the gate was told the old code no longer reads what
was removed. If that was wrong, the previous image will fail against the current schema and rolling
back makes things worse, not better. Check first:

```bash
git diff --name-only <previous-tag-sha> <current-sha> -- db/migrations/ \
  | xargs grep -l 'mageride:expand-contract' 2>/dev/null
```

Empty output → roll back freely. Any output → **do not roll back**; go to §5.

There is no down-migration mechanism in this platform and that is on purpose: DbUp applies forward
only, `db/CLAUDE.md` makes a released script immutable, and a reverse script is a second untested
DDL path that runs for the first time during an incident.

---

## 5. When a rollback is not the answer

**Fix forward.** Three cases:

1. **The migration cannot be tolerated by the previous image** (§4). Ship a corrective migration that
   restores what the old code reads — as a new `NNNN__*.sql`, never by editing the one that ran — and
   deploy that.
2. **The bad release has already written data the previous version misreads.** A rollback makes the
   corruption invisible rather than absent. Stop the writes first (`kubectl -n mageride scale
   deploy/<writer> --replicas=0` is acceptable here — it is a deliberate outage, and ArgoCD's
   `ignoreDifferences` on `/spec/replicas` means selfHeal will not undo it), then decide.
3. **Only one service is broken.** A rollback moves all 34 images. It is still usually the right
   call — a mixed-version platform is harder to reason about than an old one — but if the broken
   service is isolated and the release contained a schema change, rolling back one service is
   possible:
   ```bash
   kubectl -n mageride set image deploy/<svc> <svc>=ghcr.io/mageride/<svc>:<old-tag>
   kubectl -n mageride annotate deploy/<svc> argocd.argoproj.io/compare-options=IgnoreExtraneous
   ```
   **This diverges the cluster from the repository and selfHeal will revert it.** Do it only with a
   revert commit already in flight, and write down that you did.

---

## Verifying the rollback actually took

A rollback that reports success and changed nothing is the failure mode to watch for — usually
because the old images were pruned from the registry.

```bash
# Every pod is on the tag you asked for
kubectl -n mageride get pods -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.containers[0].image}{"\n"}{end}' \
  | awk '{print $2}' | sort | uniq -c

# Nothing is stuck pulling
kubectl -n mageride get pods --field-selector=status.phase!=Running

# The gateway answers
curl -sf https://api.mageride.lk/health/ready
```

If pods are in `ImagePullBackOff`, the images for that tag are gone. Rebuild them and re-run the
rollback:

```bash
gh workflow run images.yml -f sha=<the full commit sha for that tag>
```

---

## What not to do

- **Do not `kubectl rollout undo`.** selfHeal reverts it. See above.
- **Do not force-push `main` to remove the bad deploy commit.** ArgoCD tracks `main`; a rewritten
  history is a revision it may already have synced, and `rollback.yml` reads that history to find the
  previous tag.
- **Do not roll back the `mageride-secrets` Application.** It carries no version. If a credential is
  the problem, fix the value in Vault — ESO re-syncs within an hour, or immediately with
  `kubectl -n mageride annotate externalsecret <name> force-sync=$(date +%s) --overwrite`.
- **Do not skip the reason.** `-f reason` is required, it lands in the commit message, and
  `git log --follow infra/k8s/overlays/production/images/kustomization.yaml` is the deploy history
  somebody will read during the next incident.
