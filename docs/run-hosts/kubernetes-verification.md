# Verifying the Kubernetes run host on a cluster

The unit and integration tests run against a fake Kubernetes API. They cannot show that a real cluster schedules, isolates and cleans up the pods, so this is checked once by hand. It takes about 30 minutes and needs a cluster with a CNI that enforces NetworkPolicy. The commands use `ild` for ILD's namespace and `ild-runs` for the run namespace; use your own names.

## 1. Set up

1. Create the optional Longhorn class for run scratch (or skip it to use the cluster's default class):

   ```sh
   kubectl apply -f docs/kubernetes/storageclass-longhorn-example.yaml
   ```

2. Apply the RBAC. The ServiceAccount is `ild` in namespace `ild`:

   ```sh
   kubectl apply -f docs/kubernetes/serviceaccount.yaml -f docs/kubernetes/role.yaml \
     -f docs/kubernetes/rolebinding.yaml -f docs/kubernetes/storageclass-reader-optional.yaml \
     -f docs/kubernetes/kubernetes-endpoints-reader-optional.yaml
   ```

3. Set on the ILD Deployment (`serviceAccountName: ild`, and the pods labelled `app.kubernetes.io/name=ild`):

   ```
   ILD_API_URL=http://<ild service>.ild.svc:8080
   ILD_K8S_NAMESPACE=ild-runs
   ILD_K8S_RUNNER_IMAGE=<the ild-runner image of the version ILD runs>
   ILD_K8S_STORAGE_CLASS=ild-run-scratch
   ILD_NETWORK_PROXY_PORT=3128
   ```

   If the `ild` namespace denies ingress by default, allow pods labelled `app.kubernetes.io/managed-by=ild` from `ild-runs` to reach ports 8080 and 3128 of ILD's pods.

4. Restart ILD and read its log. Expected: no startup failure; a warning only if the storage class's `reclaimPolicy` is not `Delete`. Check the class: `kubectl get storageclass ild-run-scratch -o jsonpath='{.reclaimPolicy}'` prints `Delete`.

## 2. A full Develop run

Start a Develop loop on a small work item and watch the objects: `kubectl -n ild-runs get configmap,pvc,pod,networkpolicy -l app.kubernetes.io/managed-by=ild -w`.

| Step | Expected |
| --- | --- |
| Run reaches its Start node | One Secret, PVC, NetworkPolicy and Pod, all named `ild-run-<run id>` and labelled `ild/instance` and `ild/run`. The PVC, NetworkPolicy and Pod have an owner reference to the Secret: `kubectl -n ild-runs get pod ild-run-<id> -o jsonpath='{.metadata.ownerReferences[0].kind}'` prints `Secret` |
| Pod comes up | `Running` and Ready within the pull time; `kubectl -n ild-runs get pod -o wide` shows pods of different runs on different nodes when more than one runs |
| Agent turns | The agent node completes; the run's event log shows the agent's output as it streams. `kubectl -n ild-runs exec ild-run-<id> -- ls /ild-run/<id without dashes>/repo` shows the checkout |
| Human reply | The run waits at the Human node; the pod stays `Running`. Answer in the UI and the next node runs in the same pod |
| Preview | Start the preview from the work item's Preview tab; the preview URL loads through the wildcard proxy |
| PR push | The PR node pushes the branch and opens the PR. The pod never held the forge credential: `kubectl -n ild-runs exec ild-run-<id> -- env` shows no token, and `git -C <repo dir> remote -v` shows the remote URL without credentials |
| Cleanup | When the work item's worktree is cleaned up, `kubectl -n ild-runs get secret,pvc,pod,networkpolicy -l ild/run=<run id without dashes>` prints nothing within a minute, and the Longhorn volume is gone |

## 3. Isolation

From inside a run's pod (`kubectl -n ild-runs exec -it ild-run-<id> -- bash`), each of these must fail or time out:

- `curl -m 5 http://<another run's pod ip>:<any port>` (another run)
- `curl -m 5 https://kubernetes.default.svc` (the Kubernetes API)
- `curl -m 5 http://<postgres service>:5432` (the database)
- `ls /var/run/secrets/kubernetes.io/serviceaccount` (no token is mounted)
- `id` prints a non-root uid, and `grep Cap /proc/self/status` shows `CapEff: 0000000000000000`

And this must work: `curl -m 5 -s -o /dev/null -w '%{http_code}\n' http://<ild service>.ild.svc:8080/health`.

## 4. A killed ILD leaves nothing behind

This makes ILD fail to delete a run, then lets the sweep clean up.

1. Start a run and wait for its pod to be `Running`.
2. Take the `delete` verb off the Role: `kubectl -n ild-runs edit role ild-run-host`, and remove `delete` from the `configmaps` rule (the pods and claims rule too, for the check below).
3. Cancel the run and clean its worktree up in the UI. ILD cannot delete the ConfigMap and logs a failure; the objects are still there.
4. Put the `delete` verb back, then kill ILD without a graceful stop: `kubectl -n ild delete pod <ild pod> --grace-period=0 --force`.
5. When ILD is back, the startup sweep has removed them: `kubectl -n ild-runs get secret,pvc,pod,networkpolicy -l ild/instance=<instance>` lists nothing for the cancelled run, and ILD's log says `Removed the objects of run <id>, which is finished or gone`.
6. Objects of a run that is still `Running` or waiting for a human are kept by the same sweep.

## 4b. A pod is never without its NetworkPolicy

1. Apply `docs/kubernetes/default-deny-networkpolicy.yaml`. In **Settings > General** the Kubernetes warning about a missing default deny is gone; delete the policy and it comes back.
2. Start a run, then in the pod: `kubectl -n ild-runs exec ild-run-<id> -- sh -c 'trap "" TERM; sleep 600' &` (a process that survives TERM), and cancel the run.
3. While the pod is `Terminating`, `kubectl -n ild-runs get networkpolicy` still lists `ild-run-<id>`; it is deleted only after the pod is gone.
4. `kubectl get ns ild-runs --show-labels` shows `pod-security.kubernetes.io/enforce=restricted`, and runs still start.

## 5. Two installs in one namespace

Start a second ILD with `ILD_K8S_INSTANCE=other` and the same namespace, start a run on each, and kill one ILD. After its restart only that instance's finished runs are swept; `kubectl get pod -l ild/instance=<the other>` still lists the other install's pod.

## What to report

For anything that differs from the Expected column, send the output of `kubectl -n ild-runs describe pod ild-run-<id>`, `kubectl -n ild-runs get events`, and ILD's log from the run's start.
