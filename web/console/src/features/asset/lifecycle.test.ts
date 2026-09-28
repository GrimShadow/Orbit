import { availableActions } from './lifecycle';

describe('availableActions', () => {
  it('offers submit from draft', () => {
    expect(availableActions('draft')).toEqual(['submit']);
  });

  it('offers approve, reject and withdraw from in_review', () => {
    expect(availableActions('in_review')).toEqual(['approve', 'reject', 'withdraw']);
  });

  it('offers publish and withdraw from approved', () => {
    expect(availableActions('approved')).toEqual(['publish', 'withdraw']);
  });

  it('offers expire and withdraw from published', () => {
    expect(availableActions('published')).toEqual(['expire', 'withdraw']);
  });

  it('offers archive and withdraw from expired', () => {
    expect(availableActions('expired')).toEqual(['archive', 'withdraw']);
  });

  it('offers only withdraw from archived (a terminal-looking but still withdrawable state)', () => {
    expect(availableActions('archived')).toEqual(['withdraw']);
  });

  it('offers nothing from an unknown or deleted status', () => {
    expect(availableActions('deleted')).toEqual([]);
    expect(availableActions('purged')).toEqual([]);
  });
});
