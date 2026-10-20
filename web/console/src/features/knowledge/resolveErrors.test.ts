import { ApiError } from '@dam/sdk-js';
import { describe, expect, it } from 'vitest';
import { classifyResolveError } from './knowledgeApi';

const problem = (status: number, code?: string, title = 'Not found', detail?: string) =>
  new ApiError(status, title, detail, undefined, code ? { code } : {});

describe('classifyResolveError', () => {
  it("trusts the server's stable code over the wording", () => {
    expect(classifyResolveError(problem(404, 'knowledge.no_rule', 'Not found', 'Nothing for this VIN'))).toBe('unknownVin');
    expect(classifyResolveError(problem(404, 'knowledge.no_document', 'Not found', 'A rule matched the rule set'))).toBe(
      'noDocument',
    );
    expect(classifyResolveError(problem(422, 'knowledge.invalid_vin'))).toBe('malformed');
  });

  it('falls back to the status and wording when there is no code', () => {
    expect(classifyResolveError(problem(404, undefined, 'No rule matches this VIN'))).toBe('unknownVin');
    expect(classifyResolveError(problem(404, undefined, 'No owner manual is available for lumen'))).toBe('noDocument');
    expect(classifyResolveError(problem(422))).toBe('malformed');
    expect(classifyResolveError(problem(403))).toBe('forbidden');
    expect(classifyResolveError(problem(500))).toBe('failed');
    expect(classifyResolveError(new Error('offline'))).toBe('failed');
  });
});
