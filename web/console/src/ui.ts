// Single import point for the shared design system.
export { Button, Input, LoadingRegion, Select, Skeleton, Table, ThumbnailGrid, Tree } from '@dam/ui-kit';
export type { Column, SelectOption, Thumb, TreeNode } from '@dam/ui-kit';
import { createElement, type ReactNode } from 'react';
export const Badge = ({ children }: { children: ReactNode }) => createElement('span', { className: 'dam-badge' }, children);
