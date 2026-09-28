// Single import point for the shared design system.
export {
  Button,
  FacetList,
  Input,
  LoadingRegion,
  Modal,
  Select,
  Skeleton,
  Table,
  Tabs,
  ThumbnailGrid,
  Tree,
  useToast,
} from '@dam/ui-kit';
export type { Column, Facet, FacetOption, SelectOption, TabItem, Thumb, ToastInput, TreeNode } from '@dam/ui-kit';
import { createElement, type ReactNode } from 'react';
export const Badge = ({ children }: { children: ReactNode }) => createElement('span', { className: 'dam-badge' }, children);
