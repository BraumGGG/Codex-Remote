import assert from "node:assert/strict";
import {
  createExecutionStatus,
  reduceExecutionStatus,
} from "../../src/CodexBridge.Host/wwwroot/execution-status.mjs";

let status = createExecutionStatus("thread-uuid");
assert.deepEqual(status, {
  threadId: "thread-uuid",
  phase: "idle",
  turnId: null,
  active: false,
});

status = reduceExecutionStatus(status, { type: "submission_started" });
assert.equal(status.phase, "submitting");
assert.equal(status.active, true);

status = reduceExecutionStatus(status, { type: "submission_accepted" });
assert.equal(status.phase, "accepted");

const stillAccepted = reduceExecutionStatus(status, {
  type: "conversation_event",
  kind: "TaskCompleted",
  turnId: "turn-older",
});
assert.equal(stillAccepted.phase, "accepted");

status = reduceExecutionStatus(status, {
  type: "conversation_event",
  kind: "TaskStarted",
  turnId: "turn-1",
});
assert.equal(status.phase, "working");
assert.equal(status.turnId, "turn-1");

status = reduceExecutionStatus(status, {
  type: "conversation_event",
  kind: "AgentMessage",
  turnId: "turn-1",
});
assert.equal(status.phase, "responding");

const unchanged = reduceExecutionStatus(status, {
  type: "conversation_event",
  kind: "TaskCompleted",
  turnId: "turn-older",
});
assert.equal(unchanged.phase, "responding");

status = reduceExecutionStatus(status, {
  type: "conversation_event",
  kind: "TaskCompleted",
  turnId: "turn-1",
});
assert.equal(status.phase, "completed");
assert.equal(status.active, false);

status = reduceExecutionStatus(status, { type: "submission_failed" });
assert.equal(status.phase, "failed");
assert.equal(status.active, false);
