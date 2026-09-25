import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthProvider';
import { apiFetch } from './client';

export interface Me {
  userId: string | null;
  email: string | null;
  displayName: string | null;
  tenantId: string;
  roles: string[];
  groups: string[];
  permissions: string[];
  attributes: Record<string, string[]>;
  accessRuleCount: number;
  inactiveRoles: string[];
  status: string | null;
}

export function useMe() {
  const { accessToken, status } = useAuth();
  return useQuery({
    queryKey: ['me'],
    queryFn: () => apiFetch<Me>('/me', accessToken),
    enabled: status === 'authenticated',
  });
}
