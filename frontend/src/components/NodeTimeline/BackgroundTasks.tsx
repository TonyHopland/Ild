import type { RunBackgroundTask } from "../../types";

/**
 * The background work the agent CLI is waiting for before it ends its launch (a command, a subagent, a monitor), as the CLI
 * reports it. Shown under the running node's live output; nothing renders when there is none.
 */
export default function BackgroundTasks({ tasks }: { tasks: RunBackgroundTask[] }) {
  if (tasks.length === 0) return null;
  return (
    <div className="wiv2-background-tasks" role="status" aria-label="Background work">
      <span className="detail-label">Waiting for background work</span>
      <ul>
        {tasks.map((task, index) => (
          <li key={`${index}-${task.kind}-${task.description}`}>
            <code>{task.subagentType ? `${task.kind}: ${task.subagentType}` : task.kind}</code>{" "}
            {task.description}
          </li>
        ))}
      </ul>
    </div>
  );
}
