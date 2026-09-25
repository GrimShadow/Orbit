import '@testing-library/jest-dom/vitest';
import * as axeMatchers from 'vitest-axe/matchers';
import { expect } from 'vitest';

expect.extend(axeMatchers);

// jsdom has no <dialog> modal support; emulate open/close so behaviour tests can run.
HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
  this.setAttribute('open', '');
};
HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
  this.removeAttribute('open');
  this.dispatchEvent(new Event('close'));
};
// jsdom lacks CSS.escape
if (!('CSS' in globalThis) || !(globalThis as { CSS?: { escape?: unknown } }).CSS?.escape) {
  (globalThis as unknown as { CSS: { escape: (s: string) => string } }).CSS = {
    escape: (s: string) => s.replace(/["\\]/g, '\\$&'),
  };
}
