import { resolveAuthority } from './config';

describe('resolveAuthority', () => {
  it('keeps full URLs as they are', () => {
    expect(resolveAuthority('http://localhost:18080/realms/dam', 'https://x.example')).toBe('http://localhost:18080/realms/dam');
  });
  it('resolves a path against the page origin', () => {
    expect(resolveAuthority('/realms/acme', 'https://demo.example.net')).toBe('https://demo.example.net/realms/acme');
  });
});
