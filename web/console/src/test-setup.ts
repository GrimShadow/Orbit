import '@testing-library/jest-dom/vitest';
import * as axeMatchers from 'vitest-axe/matchers';
import { expect } from 'vitest';

expect.extend(axeMatchers);

// jsdom has no <dialog> modal support; emulate open/close so components built on ui-kit's Modal/Drawer can be tested
// here too (same polyfill as web/ui-kit/src/test-setup.ts, which the console can't share a setup file with directly).
HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
  this.setAttribute('open', '');
};
HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
  this.removeAttribute('open');
  this.dispatchEvent(new Event('close'));
};
