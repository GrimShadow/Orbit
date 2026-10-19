import {
  addStepAfter,
  addStepFirst,
  chainOf,
  issueStepIndex,
  normalizeDefinition,
  twoLevelApproval,
  blankDefinition,
  COMPLETE,
  orderSteps,
  parseDefinition,
  removeStep,
  toJson,
  updateStep,
  validateDefinition,
  type WorkflowDefinition,
} from './definition';

const twoLevel = (): WorkflowDefinition => {
  let def = blankDefinition('Approver');
  def = updateStep(def, 'review', { slaHours: 48, escalation: { to: { roles: ['Admin'], users: [] } } });
  const added = addStepAfter(def, 'review');
  return updateStep(added.def, added.id, { id: 'final', name: 'Final approval', assignees: { roles: ['Librarian'], users: [] } });
};

describe('workflow definition helpers', () => {
  it('starts as a valid one-step workflow', () => {
    expect(validateDefinition(blankDefinition())).toEqual([]);
  });

  it('chains a new step after the one given and keeps the chain connected', () => {
    const def = twoLevel();
    expect(orderSteps(def).map((s) => s.id)).toEqual(['review', 'final']);
    expect(def.steps.find((s) => s.id === 'review')!.onApprove).toBe('final');
    expect(def.steps.find((s) => s.id === 'final')!.onApprove).toBe(COMPLETE);
    expect(validateDefinition(def)).toEqual([]);
  });

  it('inserts in the middle without breaking the end of the chain', () => {
    let def = twoLevel();
    const mid = addStepAfter(def, 'review');
    def = updateStep(mid.def, mid.id, { name: 'Legal', assignees: { roles: ['Editor'], users: [] } });
    expect(orderSteps(def).map((s) => s.id)).toEqual(['review', mid.id, 'final']);
    expect(validateDefinition(def)).toEqual([]);
  });

  it('renaming a step id re-points every reference to it', () => {
    const def = updateStep(twoLevel(), 'review', { id: 'regional' });
    expect(def.start).toBe('regional');
    expect(validateDefinition(def)).toEqual([]);
    const sendBack = updateStep(def, 'final', { onReject: 'regional' });
    expect(sendBack.steps.find((s) => s.id === 'final')!.onReject).toBe('regional');
    expect(validateDefinition(sendBack)).toEqual([]);
  });

  it('removing a step reconnects its neighbours, and removing the start moves the start', () => {
    const removedLast = removeStep(twoLevel(), 'final');
    expect(removedLast.steps).toHaveLength(1);
    expect(removedLast.steps[0]!.onApprove).toBe(COMPLETE);
    const removedFirst = removeStep(twoLevel(), 'review');
    expect(removedFirst.start).toBe('final');
    expect(validateDefinition(removedFirst)).toEqual([]);
    expect(removeStep(blankDefinition(), 'review').steps).toHaveLength(1); // never empties the workflow
  });
});

describe('validateDefinition', () => {
  it('names each problem by path', () => {
    const def = blankDefinition();
    def.steps[0]!.name = ' ';
    def.steps[0]!.assignees = { roles: [], users: [] };
    def.steps[0]!.slaHours = 0;
    def.steps[0]!.escalation = { to: { roles: [], users: [] } };
    expect(
      validateDefinition(def)
        .map((i) => i.path)
        .sort(),
    ).toEqual(['steps[0].assignees', 'steps[0].escalation', 'steps[0].name', 'steps[0].slaHours']);
  });

  it('rejects duplicate ids, dangling targets, unreachable steps and approval loops', () => {
    const dup = twoLevel();
    dup.steps[1]!.id = 'review';
    expect(validateDefinition(dup).some((i) => i.message === 'Step ids must be unique.')).toBe(true);

    const dangling = twoLevel();
    dangling.steps[1]!.onApprove = 'ghost';
    expect(validateDefinition(dangling).map((i) => i.path)).toContain('steps[1].onApprove');

    const orphan = twoLevel();
    orphan.steps[0]!.onApprove = COMPLETE; // nothing leads to "final" any more
    expect(validateDefinition(orphan).some((i) => i.message === 'Nothing leads to this step.')).toBe(true);

    const loop = twoLevel();
    loop.steps[1]!.onApprove = 'review';
    expect(validateDefinition(loop).some((i) => i.message.includes('loop'))).toBe(true);
  });

  it('allows a rejection to loop back to an earlier step', () => {
    const def = updateStep(twoLevel(), 'final', { onReject: 'review' });
    expect(validateDefinition(def)).toEqual([]);
  });
});

describe('JSON round trip', () => {
  it('serialises and parses to the same definition', () => {
    const def = twoLevel();
    const parsed = parseDefinition(toJson(def));
    expect('def' in parsed && parsed.def).toEqual(def);
  });

  it('reports unreadable input instead of throwing', () => {
    expect(parseDefinition('{')).toEqual({ error: 'That is not valid JSON.' });
    expect(parseDefinition('{"a":1}')).toEqual({ error: 'Expected an object with a "steps" list.' });
  });
});

describe('designer helpers', () => {
  it('inserts a step at the very start and makes it the start', () => {
    const r = addStepFirst(twoLevel());
    expect(r.def.start).toBe(r.id);
    expect(chainOf(r.def).chain.map((s) => s.id)).toEqual([r.id, 'review', 'final']);
    expect(chainOf(r.def).orphans).toEqual([]);
  });

  it('reports steps that nothing leads to as orphans', () => {
    const def = twoLevel();
    def.steps[0]!.onApprove = COMPLETE;
    expect(chainOf(def).chain.map((s) => s.id)).toEqual(['review']);
    expect(chainOf(def).orphans.map((s) => s.id)).toEqual(['final']);
  });

  it('twoLevelApproval mirrors the core template pack and is valid', () => {
    const def = twoLevelApproval();
    expect(validateDefinition(def)).toEqual([]);
    expect(def.steps.map((s) => [s.id, s.assignees.roles[0], s.slaHours, s.escalation?.to.roles[0]])).toEqual([
      ['review', 'Approver', 48, 'Admin'],
      ['final-approval', 'Librarian', 24, 'Admin'],
    ]);
  });

  it('maps an issue path to the index of its step', () => {
    expect(issueStepIndex('steps[2].assignees.roles[0]')).toBe(2);
    expect(issueStepIndex('start')).toBeNull();
    expect(issueStepIndex('')).toBeNull();
  });

  it('fills in the defaults a stored definition may leave out', () => {
    const def = normalizeDefinition({ start: 'a', steps: [{ id: 'a', name: 'A', assignees: { roles: ['X'] } }] });
    expect(def.steps[0]).toMatchObject({
      mode: 'any',
      slaHours: null,
      escalation: null,
      onApprove: COMPLETE,
      onReject: 'reject',
    });
    expect(def.steps[0]!.assignees).toEqual({ roles: ['X'], users: [] });
    expect(def.distinctApprovers).toBe(true);
  });

  it('keeps properties it does not know, so layout hints survive an edit', () => {
    const raw = {
      version: 1,
      start: 'a',
      layout: { zoom: 2 },
      steps: [{ id: 'a', name: 'A', assignees: { roles: ['X'], users: [] }, x: 10 }],
    };
    const def = normalizeDefinition(raw) as unknown as typeof raw;
    expect(def.layout).toEqual({ zoom: 2 });
    expect((def.steps[0] as { x?: number }).x).toBe(10);
    const edited = updateStep(def as never, 'a', { name: 'B' }) as unknown as typeof raw;
    expect((edited.steps[0] as { x?: number }).x).toBe(10);
  });
});
