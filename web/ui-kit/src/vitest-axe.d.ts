import 'vitest';

declare module 'vitest' {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any, @typescript-eslint/no-empty-object-type
  interface Assertion<T = any> {
    toHaveNoViolations(): void;
  }
}
