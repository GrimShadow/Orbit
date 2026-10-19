/** The workflow definition document (JSON v1) and the pure helpers the designer edits it with. The server validates the
 * same rules authoritatively (`POST /workflows/validate`); `validateDefinition` mirrors them so the designer can flag a
 * problem on every keystroke without a round trip. */
export type StepMode = 'any' | 'all';
export interface Assignees {
  roles: string[];
  users: string[];
}
export interface WorkflowStep {
  id: string;
  name: string;
  assignees: Assignees;
  mode: StepMode;
  slaHours: number | null;
  escalation: { to: Assignees } | null;
  /** A step id, or `complete` to finish the workflow approved. */
  onApprove: string;
  /** `reject` returns the asset to draft; a step id sends it back to that step. */
  onReject: string;
}
export interface WorkflowDefinition {
  version: 1;
  start: string;
  autoPublish: boolean;
  distinctApprovers: boolean;
  steps: WorkflowStep[];
}
export interface DefinitionIssue {
  /** JSON-path-ish location, e.g. `steps[1].assignees` - the same shape the server returns. */
  path: string;
  message: string;
}

export const COMPLETE = 'complete';
export const REJECT = 'reject';
export const MAX_STEPS = 20;
export const ID_PATTERN = /^[a-z0-9_-]{1,40}$/;

export function emptyAssignees(): Assignees {
  return { roles: [], users: [] };
}

export function newStep(existing: readonly WorkflowStep[], overrides: Partial<WorkflowStep> = {}): WorkflowStep {
  const taken = new Set(existing.map((s) => s.id));
  let n = existing.length + 1;
  while (taken.has(`step-${n}`)) n++;
  return {
    id: `step-${n}`,
    name: '',
    assignees: emptyAssignees(),
    mode: 'any',
    slaHours: null,
    escalation: null,
    onApprove: COMPLETE,
    onReject: REJECT,
    ...overrides,
  };
}

/** A fresh one-step workflow: one reviewer role approves, then it is done. */
export function blankDefinition(role = 'Approver'): WorkflowDefinition {
  const step = newStep([], { id: 'review', name: 'Review', assignees: { roles: [role], users: [] } });
  return { version: 1, start: step.id, autoPublish: false, distinctApprovers: true, steps: [step] };
}

/** Steps in the order a submission walks them (follow `onApprove` from `start`), then any that are unreachable. */
export function orderSteps(def: WorkflowDefinition): WorkflowStep[] {
  const byId = new Map(def.steps.map((s) => [s.id, s]));
  const out: WorkflowStep[] = [];
  const seen = new Set<string>();
  for (let cur = byId.get(def.start); cur && !seen.has(cur.id); cur = byId.get(cur.onApprove)) {
    out.push(cur);
    seen.add(cur.id);
  }
  return [...out, ...def.steps.filter((s) => !seen.has(s.id))];
}

/** The approval chain from `start`, and the steps nothing in it leads to. */
export function chainOf(def: WorkflowDefinition): { chain: WorkflowStep[]; orphans: WorkflowStep[] } {
  const byId = new Map(def.steps.map((s) => [s.id, s]));
  const chain: WorkflowStep[] = [];
  const seen = new Set<string>();
  for (let cur = byId.get(def.start); cur && !seen.has(cur.id); cur = byId.get(cur.onApprove)) {
    chain.push(cur);
    seen.add(cur.id);
  }
  return { chain, orphans: def.steps.filter((s) => !seen.has(s.id)) };
}

/** Inserts a new step at the very start, so a submission reaches it before anything else. */
export function addStepFirst(def: WorkflowDefinition): { def: WorkflowDefinition; id: string } {
  const step = newStep(def.steps, { onApprove: def.steps.some((s) => s.id === def.start) ? def.start : COMPLETE });
  return { def: { ...def, start: step.id, steps: [...def.steps, step] }, id: step.id };
}

/** Inserts a new step directly after `afterId` in the approval chain (or at the end when omitted). */
export function addStepAfter(def: WorkflowDefinition, afterId?: string): { def: WorkflowDefinition; id: string } {
  const ordered = orderSteps(def);
  const after = def.steps.find((s) => s.id === (afterId ?? ordered.at(-1)?.id));
  const step = newStep(def.steps, { onApprove: after?.onApprove ?? COMPLETE });
  const steps = def.steps.map((s) => (s.id === after?.id ? { ...s, onApprove: step.id } : s));
  return { def: { ...def, start: def.start || step.id, steps: [...steps, step] }, id: step.id };
}

/** Removes a step and re-points everything that led to it at what it led to, so the chain stays connected. */
export function removeStep(def: WorkflowDefinition, id: string): WorkflowDefinition {
  const gone = def.steps.find((s) => s.id === id);
  if (!gone || def.steps.length <= 1) return def;
  const next = gone.onApprove === id ? COMPLETE : gone.onApprove;
  const retarget = (target: string, fallback: string) => (target === id ? fallback : target);
  const steps = def.steps
    .filter((s) => s.id !== id)
    .map((s) => ({ ...s, onApprove: retarget(s.onApprove, next), onReject: retarget(s.onReject, REJECT) }));
  return { ...def, start: def.start === id ? (next === COMPLETE ? (steps[0]?.id ?? '') : next) : def.start, steps };
}

export function updateStep(def: WorkflowDefinition, id: string, patch: Partial<WorkflowStep>): WorkflowDefinition {
  // Renaming the id re-points every reference to it.
  const newId = patch.id;
  const rename = (v: string) => (newId && v === id ? newId : v);
  return {
    ...def,
    start: rename(def.start),
    steps: def.steps.map((s) =>
      s.id === id
        ? { ...s, ...patch, onApprove: patch.onApprove ?? rename(s.onApprove), onReject: patch.onReject ?? rename(s.onReject) }
        : { ...s, onApprove: rename(s.onApprove), onReject: rename(s.onReject) },
    ),
  };
}

const nonEmpty = (a: Assignees) => a.roles.length + a.users.length > 0;

export function validateDefinition(def: WorkflowDefinition): DefinitionIssue[] {
  const issues: DefinitionIssue[] = [];
  const add = (path: string, message: string) => issues.push({ path, message });
  if (def.steps.length < 1) add('steps', 'Add at least one step.');
  if (def.steps.length > MAX_STEPS) add('steps', `A workflow can have at most ${MAX_STEPS} steps.`);
  const ids = new Set<string>();
  def.steps.forEach((s, i) => {
    const p = `steps[${i}]`;
    if (!ID_PATTERN.test(s.id)) add(`${p}.id`, 'Use 1-40 lowercase letters, digits, "-" or "_".');
    else if (ids.has(s.id)) add(`${p}.id`, 'Step ids must be unique.');
    ids.add(s.id);
    if (!s.name.trim()) add(`${p}.name`, 'Give the step a name.');
    if (!nonEmpty(s.assignees)) add(`${p}.assignees`, 'Choose at least one role or person to decide this step.');
    if (s.slaHours !== null && (!Number.isInteger(s.slaHours) || s.slaHours < 1 || s.slaHours > 8760))
      add(`${p}.slaHours`, 'Use a whole number of hours from 1 to 8760.');
    if (s.escalation && !nonEmpty(s.escalation.to)) add(`${p}.escalation`, 'Choose who an overdue task escalates to.');
  });
  if (def.steps.length > 0 && !ids.has(def.start)) add('start', 'The start step does not exist.');
  def.steps.forEach((s, i) => {
    if (s.onApprove !== COMPLETE && !ids.has(s.onApprove)) add(`steps[${i}].onApprove`, 'Points at a step that does not exist.');
    if (s.onReject !== REJECT && !ids.has(s.onReject)) add(`steps[${i}].onReject`, 'Points at a step that does not exist.');
  });
  // Every step must be reachable, and approving must never loop.
  const byId = new Map(def.steps.map((s) => [s.id, s]));
  const reachable = new Set<string>();
  const walk = (id: string) => {
    if (reachable.has(id) || !byId.has(id)) return;
    reachable.add(id);
    const s = byId.get(id)!;
    walk(s.onApprove);
    walk(s.onReject);
  };
  walk(def.start);
  def.steps.forEach((s, i) => {
    if (ids.has(s.id) && !reachable.has(s.id)) add(`steps[${i}]`, 'Nothing leads to this step.');
  });
  const state = new Map<string, 1 | 2>();
  const loops = (id: string): boolean => {
    if (state.get(id) === 2) return false;
    if (state.get(id) === 1) return true;
    state.set(id, 1);
    const s = byId.get(id);
    const hit = s && s.onApprove !== COMPLETE && byId.has(s.onApprove) ? loops(s.onApprove) : false;
    state.set(id, 2);
    return hit;
  };
  if (def.steps.some((s) => loops(s.id))) add('steps', 'Approving steps must not loop back on themselves.');
  return issues;
}

export function parseDefinition(text: string): { def: WorkflowDefinition } | { error: string } {
  let raw: unknown;
  try {
    raw = JSON.parse(text);
  } catch {
    return { error: 'That is not valid JSON.' };
  }
  if (!raw || typeof raw !== 'object' || !Array.isArray((raw as { steps?: unknown }).steps))
    return { error: 'Expected an object with a "steps" list.' };
  const r = raw as Partial<WorkflowDefinition> & { steps: Partial<WorkflowStep>[] };
  // Properties the designer does not know (layout hints, future fields) ride along untouched, as the server keeps them too.
  const steps: WorkflowStep[] = r.steps.map((s, i) => ({
    ...s,
    id: String(s.id ?? `step-${i + 1}`),
    name: String(s.name ?? ''),
    assignees: { roles: [...(s.assignees?.roles ?? [])], users: [...(s.assignees?.users ?? [])] },
    mode: s.mode === 'all' ? 'all' : 'any',
    slaHours: typeof s.slaHours === 'number' ? s.slaHours : null,
    escalation: s.escalation
      ? { to: { roles: [...(s.escalation.to?.roles ?? [])], users: [...(s.escalation.to?.users ?? [])] } }
      : null,
    onApprove: String(s.onApprove ?? COMPLETE),
    onReject: String(s.onReject ?? REJECT),
  }));
  return {
    def: {
      ...r,
      version: 1,
      start: String(r.start ?? steps[0]?.id ?? ''),
      autoPublish: Boolean(r.autoPublish),
      distinctApprovers: r.distinctApprovers !== false,
      steps,
    },
  };
}

export const toJson = (def: WorkflowDefinition) => JSON.stringify(def, null, 2);

/** The `core` template pack's "Two-level approval" (ADR-0030): review by Approver in 48 h, then final approval by
 * Librarian in 24 h, each escalating to Admin when late. */
export function twoLevelApproval(): WorkflowDefinition {
  const escalation = () => ({ to: { roles: ['Admin'], users: [] as string[] } });
  const review = newStep([], {
    id: 'review',
    name: 'Review',
    assignees: { roles: ['Approver'], users: [] },
    slaHours: 48,
    escalation: escalation(),
    onApprove: 'final-approval',
  });
  const final = newStep([review], {
    id: 'final-approval',
    name: 'Final approval',
    assignees: { roles: ['Librarian'], users: [] },
    slaHours: 24,
    escalation: escalation(),
  });
  return { version: 1, start: review.id, autoPublish: false, distinctApprovers: true, steps: [review, final] };
}

/** The index in `steps[]` an issue path points into (`steps[1].assignees.roles[0]` -> 1), or null for workflow-level issues. */
export function issueStepIndex(path: string): number | null {
  const m = /^steps\[(\d+)\]/.exec(path);
  return m ? Number(m[1]) : null;
}

/** Brings a definition as stored (every optional field may be missing) to the full shape the designer edits. */
export function normalizeDefinition(raw: unknown): WorkflowDefinition {
  const parsed = parseDefinition(JSON.stringify(raw ?? {}));
  return 'def' in parsed ? parsed.def : blankDefinition();
}
