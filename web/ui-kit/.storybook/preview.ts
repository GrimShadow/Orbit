import type { Preview } from '@storybook/react';
import '../src/styles.css';

const preview: Preview = {
  parameters: { layout: 'padded', a11y: { test: 'error' } },
  globalTypes: {
    theme: { description: 'Theme', toolbar: { icon: 'mirror', items: ['light', 'dark'], dynamicTitle: true } },
  },
  decorators: [
    (Story, ctx) => {
      document.documentElement.dataset.theme = (ctx.globals.theme as string) ?? 'light';
      return Story();
    },
  ],
  initialGlobals: { theme: 'light' },
};
export default preview;
