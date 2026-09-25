// Single import point for the shared design system.
export { Button, LoadingRegion, Skeleton } from '@dam/ui-kit';
import { createElement, type ReactNode } from 'react';
export const Badge = ({ children }: { children: ReactNode }) => createElement('span', { className: 'dam-badge' }, children);
