import '@testing-library/jest-dom/vitest';
import * as axeMatchers from 'vitest-axe/matchers';
import { configure } from '@testing-library/react';
import { expect } from 'vitest';

expect.extend(axeMatchers);

// findBy*/waitFor give up after 1 s by default, which a busy CI runner (two cores, several test files in parallel) can
// miss for work that takes a moment under load. This is only how long a wait may last before it fails, so passing tests
// are not slower.
configure({ asyncUtilTimeout: 8000 });

// jsdom has no <dialog> modal support; emulate open/close so components built on ui-kit's Modal/Drawer can be tested
// here too (same polyfill as web/ui-kit/src/test-setup.ts, which the console can't share a setup file with directly).
HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
  this.setAttribute('open', '');
};
HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
  this.removeAttribute('open');
  this.dispatchEvent(new Event('close'));
};
