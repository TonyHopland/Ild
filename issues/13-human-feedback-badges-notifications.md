## Parent

PRD.md

## Status

**COMPLETE**

## What to build

Enhance the Human Feedback status column on the taskboard to display reason badges indicating why each WorkItem needs attention. Reasons include: PR awaiting merge, node failure exhaustion, rebase conflict, Human node input. Add browser notifications when a WorkItem transitions to Human Feedback.

The `HumanFeedbackReason` is stored on the WorkItem or derived from the LoopRun state. Badges are color-coded and displayed on the WorkItem card.

## Acceptance criteria

- [ ] WorkItem model or LoopRun tracks `HumanFeedbackReason` (string or enum)
- [ ] Human Feedback column cards display a badge showing the reason (e.g., "PR Awaiting Merge", "Node Failed", "Rebase Conflict", "Human Input Needed")
- [ ] Badges are visually distinct (color/icon per reason type)
- [ ] SignalR `WorkItemHub.NotifyHumanFeedbackRequired` pushes reason data to the frontend
- [ ] Browser notification fires when a WorkItem enters Human Feedback (using Notification API)
- [ ] Notification request handles browser permission prompt
- [ ] Settings page has a toggle to enable/disable browser notifications
- [ ] Frontend tests cover: badge rendering per reason, badge appears on card, notification toggle persists
- [ ] Backend tests cover: HumanFeedbackReason is set on transition, SignalR notification includes reason
- [ ] `vp check` and `vp test` pass

## Blocked by

None - can start immediately
