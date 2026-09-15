export function createExecutionStatus(threadId) {
  return {
    threadId,
    phase: "idle",
    turnId: null,
    active: false,
  };
}

export function reduceExecutionStatus(status, event) {
  if (event.type === "submission_started") {
    return { ...status, phase: "submitting", turnId: null, active: true };
  }
  if (event.type === "submission_accepted") {
    return { ...status, phase: "accepted", active: true };
  }
  if (event.type === "submission_failed") {
    return { ...status, phase: "failed", active: false };
  }
  if (event.type !== "conversation_event") return status;

  if (event.kind === "TaskStarted") {
    return {
      ...status,
      phase: "working",
      turnId: event.turnId || null,
      active: true,
    };
  }
  if (event.kind === "AgentMessage" && hasStarted(status) && matchesTurn(status, event)) {
    return { ...status, phase: "responding" };
  }
  if (event.kind === "TaskCompleted" && hasStarted(status) && matchesTurn(status, event)) {
    return { ...status, phase: "completed", active: false };
  }
  return status;
}

function hasStarted(status) {
  return status.phase === "working" || status.phase === "responding";
}

function matchesTurn(status, event) {
  return !status.turnId || !event.turnId || status.turnId === event.turnId;
}
