import { unwrap, type components } from '@dam/sdk-js';
import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthProvider';
import { useApiClient } from './apiClient';

export type Me = components['schemas']['MeDto'];

export function useMe() {
  const { status } = useAuth();
  const api = useApiClient();
  return useQuery({
    queryKey: ['me'],
    queryFn: async () => unwrap(await api.GET('/api/v1/me')),
    enabled: status === 'authenticated',
  });
}
