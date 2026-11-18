# ADR-0040: A run's work does not live only in its pod

Work item 696 lost every unpushed change when its run pod disappeared; a retry then failed on the exec websocket with a raw 404, because the Kubernetes host revived only a pod that still existed in phase Failed or Succeeded.

## Decision

- **Make a missing pod again.** The run's owner ConfigMap now records what the run was made from (base path, branch, repository, DNS and preview-data flags). When a command meets a pod that is not there, the Kubernetes host builds the pod again from that record, on the run's claim if it survived, and logs what came back. A run made before this has no record and is reported as lost.
- **Say so when it cannot.** A command in a run whose pod is gone and cannot be made again fails with `RunPodLostException`: "The run pod no longer exists" with the way on. `RunTerminationKind.PodLost` is what a node failed by a vanished pod reports. The API answers 409 with the same text for any request (preview, the worktree tools, the chat's reads) that reached a lost pod, instead of a 500 or a raw status.
- **Keep the work outside the pod: a checkpoint, not a branch.** After every node and before the run waits for a person, the host commits everything git does not ignore (tracked, changed and untracked) on a temporary index, and bundles it into ILD's base repository as the hidden ref `refs/ild/runs/<run>`. Nothing is pushed to the remote, so no CI runs, no half-done branch appears for review, and nothing `.gitignore` keeps out (secrets such as `.env`) leaves the pod. The ref is removed when the run's environment is deleted. A pod made again after its volume was lost checks the branch out and puts the checkpoint back: its commits, and its uncommitted and untracked files as uncommitted edits.
- **Why not only a push, why not only a volume.** A push of the run's branch would publish half-done work and trip CI and review, and still leave uncommitted files in the pod. The per-run volume the Kubernetes host already has keeps uncommitted edits but is deleted with the run's ConfigMap or lost with its node's storage. The volume stays the first source; the checkpoint covers its loss.
- **What a checkpoint leaves unprotected.** What the run did after the last checkpoint (a node in progress), and files git ignores. A checkpoint is best effort: it is bounded in time and never stops a run.
- **Prevention.** A sweep keeps the objects of every run that is not Completed or Cancelled (Running, waiting, halted, failed), and the idle reaper touches only the chat pod; each has a test. Run pods carry `cluster-autoscaler.kubernetes.io/safe-to-evict: "false"`, so a node scale-down does not drain them.
- **A lost pod is noticed, not only met.** A watchdog checks every minute that a `Running` run still has its pod or container; two checks in a row that find it missing park the run as Node Failed with the pod-lost reason (the same park a crash makes, so Retry works), and cut off whatever the loop was waiting on.
- **What is told on the run.** What a recreated pod got back, and what it did not, is an event on the run (`RunRecovered`); a checkpoint that could not be kept is one too (`CheckpointFailed`).
- **When a checkpoint is made.** After every node (succeeded, failed, interrupted), when a node waits for an answer, and when a halt cuts a node off. A run parked for a long time relies on the checkpoint taken as it parked: nothing changes in a parked pod, but if its volume and pod are lost after that, the checkpoint is what comes back, and an agent that was still writing when a halt landed loses what it wrote after the halt's checkpoint.
- **Verified.** `KubernetesRunHostTests` deletes a run's pod and volume after a checkpoint and starts a command: the pod is made again and the checkout is restored from the checkpoint with the real scripts, with its commit, edit and untracked file back.
- **Not done: reading the pushed branch.** The worktree tools and preview report a lost pod, but do not yet fall back to reading the run's pushed branch when there is one.
- **Not done: the Docker host.** It keeps no record of a run's request to make a container from and checkpoints nothing; a missing container is reported as lost, and a stopped one is still started again (as before).

## Root cause of work item 696's pod loss

From cluster metrics and logs, 17 Nov (UTC). It was not an ILD action.

- The run pod requested 256Mi of memory with a 6Gi limit and was scheduled on the smallest node (8 GiB; the others have 16 GiB), next to the database and other workloads.
- 18:08-18:38: the run pod grew from 0.14 to 4.0 GiB while the node's available memory fell from 1.8 to 0.5 GiB.
- 18:39: the node went NotReady and got the unreachable taint. The database on it stalled; that is the "transient failure" that failed the Develop node at 18:41.
- 18:44: the default 300 s toleration expired and the taint manager deleted every pod on the node. ILD and the database came back through their controllers. The run pod is a bare pod, so nothing made it again.
- 18:45 on: startup reconciliation and orphan recovery met the exec 404 every minute, which is why the run stayed Running.
- No node reboot, no OOM kill, and no ILD sweep or idle cleanup was involved. The run's volume claim and ConfigMap survived until the replacement run started and ILD deleted the old run's environment: the work was lost by that cleanup, not with the pod.

### What protects a run pod from a NotReady node, and what does not

- **Does not:** the cluster-autoscaler `safe-to-evict` annotation (it only governs autoscaler drains, not taint-based eviction), and the memory limit (the node failed under the sum of its workloads, not on an OOM kill).
- **Reduces the odds:** `ILD_K8S_RUN_NODE_SELECTOR` keeps runs off the node that holds the database; the default memory request is now 1Gi, so the scheduler no longer counts a growing run as nearly free; `ILD_K8S_RUN_TOLERATION_SECONDS` lets a pod stay longer on a node that may come back.
- **What actually saves the work:** nothing stops a node from being lost. The pod is made again on its surviving volume (the claim and its ConfigMap outlive the pod), and the checkpoint covers a lost volume.
- **Existing runs:** a run made before this version gets its record patched into its ConfigMap when its start meets it; this needs the `patch` verb on `configmaps` in ILD's Role (`docs/kubernetes/role.yaml`). Without it ILD warns and such a run is reported as lost, as before.

### Follow-ups, not in this change

- A plain read (a GET) can make a pod again, waiting on the repository lock inside the request.
- A checkpoint on the cancel path can race the removal of the run's environment.
- Checkpoint refs are removed only when the run's environment is deleted, not on the sweep's teardown; a new run's checkout bundles every ref, including other runs' checkpoints. Exclude `refs/ild/` from that bundle and remove the ref on every teardown.
- The checkpoint script rebuilds its index from `HEAD`, so `git add -A` rehashes every tracked file; seed it from the run's own index.
