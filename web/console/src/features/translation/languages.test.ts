import { describe, expect, it } from 'vitest';
import { ENABLED_LANGUAGES, tenantLanguages } from './languages';

describe('tenantLanguages', () => {
  it('reads the languages a tenant has chosen, lower-cased and de-duplicated', () => {
    expect(tenantLanguages({ languages: ['EN', 'hi', ' en ', 'ta'] })).toEqual(['en', 'hi', 'ta']);
  });

  it('is empty when the tenant names none, so the fallback applies', () => {
    expect(tenantLanguages({ languages: [] })).toEqual([]);
    expect(tenantLanguages({})).toEqual([]);
    expect(tenantLanguages(null)).toEqual([]);
    expect(tenantLanguages({ languages: 'en' })).toEqual([]);
  });

  it('ignores entries that are not language codes', () => {
    expect(tenantLanguages({ languages: ['hi', 3, null, '', 'te'] })).toEqual(['hi', 'te']);
  });

  it('keeps the five index languages as the fallback', () => {
    expect(ENABLED_LANGUAGES).toEqual(['en', 'hi', 'mr', 'ta', 'te']);
  });
});
