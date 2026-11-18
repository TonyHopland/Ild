# ADR-0040: A run's work does not live only in its pod

A run's pod is a bare pod: when its node goes NotReady the cluster deletes it after the toleration period, and nothing makes it again. Work item 696 lost its pod that way (the node ran out of memory under a run whose memory request was far below what it used), every later command failed on a raw 404 from the exec API, and the run stayed `Running` with nothing driving it. The run's volume had survived, but nothing used it.

## Decision

- **A lost pod is made again before a node runs, and nowhere else.** The run's owner ConfigMap records what the run was made from. Before each node of a `Running` run, and before a recovered run is resumed, ILD asks the host to recover the run: a pod that no longer exists is made again from that record, on the run's volume if it survived, and what came back is an event on the run (`RunRecovered`). A command never makes a pod: recreation holds the repository's lock and can take minutes, which neither a read behind an API request nor a command inside another creation may do.
- **A command in a lost pod says so.** It fails with `RunPodLostException`, which names the way on (retry the node). The API answers such a request 409 with code `run-pod-lost`; the work item itself still opens and carries the message.
- **A lost pod is noticed, not only met.** A watchdog checks every minute that a `Running` run still has its pod. Two checks in a row that find it missing park the run as Node Failed, so a retry makes the pod again.
- **The work is also kept outside the pod, as a checkpoint and not as a branch.** After every node, and when a node parks for a person or is halted, the host commits everything git does not ignore on a temporary index and keeps it in ILD's base repository as the hidden ref `refs/ild/runs/<run>`. A pod made again without its volume gets its commits back, and its uncommitted and untracked files as uncommitted edits. The ref is never given to another run and goes when the run's environment is removed.
- **Where run pods go is the operator's to say.** `ILD_K8S_RUN_NODE_SELECTOR` keeps runs off chosen nodes, `ILD_K8S_RUN_TOLERATION_SECONDS` lets a pod outlast a node that comes back, and a run pod's default memory request is 1Gi so the scheduler counts it.

## Why

- **Not a pushed branch.** It would publish half-done work, trip CI and review, and still leave uncommitted files in the pod.
- **Not the volume alone.** It keeps everything while it exists, but it goes with the run's ConfigMap or its node's storage. The volume is the first source and the checkpoint covers its loss.
- **Not recreation from inside a command.** That was tried first. It made any read recreate pods, and it needed the repository's lock from places that already held it.

## Limits

- A checkpoint is best effort and bounded in time. What a run did after its last checkpoint, and files git ignores, are lost with the volume.
- Nothing stops a node from being lost. The settings above lower the odds; recovery is what saves the work.
- A run made before this version has no record until its Start node runs again, which needs the `patch` verb on `configmaps`. Until then its lost pod is reported as lost.
- The Docker host keeps no record and no checkpoint: a missing container is reported as lost.
- The worktree tools and the preview report a lost pod; they do not fall back to the pushed branch.
