import { UserManager, WebStorageStateStore, type User } from 'oidc-client-ts';
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { config } from '../config';

export interface AuthState {
  status: 'loading' | 'authenticated' | 'unauthenticated';
  name?: string;
  email?: string;
  accessToken?: string;
  signIn: (returnTo?: string) => Promise<void>;
  signOut: () => Promise<void>;
}

export const AuthContext = createContext<AuthState | null>(null);

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>');
  return ctx;
}

let manager: UserManager | undefined;
export function getUserManager(): UserManager {
  // Authorization-code flow + PKCE (oidc-client-ts default). Tokens stay in sessionStorage, not localStorage.
  manager ??= new UserManager({
    authority: config.oidcAuthority,
    client_id: config.oidcClientId,
    redirect_uri: `${window.location.origin}/auth/callback`,
    post_logout_redirect_uri: `${window.location.origin}/login`,
    response_type: 'code',
    scope: 'openid',
    automaticSilentRenew: true, // refresh-token rotation; access tokens live <= 15 min
    userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  });
  return manager;
}

function toState(user: User | null): Pick<AuthState, 'status' | 'name' | 'email' | 'accessToken'> {
  if (!user || user.expired) return { status: 'unauthenticated' };
  return {
    status: 'authenticated',
    name: (user.profile.name as string | undefined) ?? (user.profile.preferred_username as string | undefined),
    email: user.profile.email,
    accessToken: user.access_token,
  };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const um = getUserManager();
  const [state, setState] = useState<ReturnType<typeof toState>>({ status: 'loading' });

  useEffect(() => {
    let alive = true;
    um.getUser()
      .then((u) => alive && setState(toState(u)))
      .catch(() => alive && setState({ status: 'unauthenticated' }));
    const onLoaded = (u: User) => setState(toState(u));
    const onGone = () => setState({ status: 'unauthenticated' });
    um.events.addUserLoaded(onLoaded);
    um.events.addUserUnloaded(onGone);
    um.events.addSilentRenewError(onGone);
    return () => {
      alive = false;
      um.events.removeUserLoaded(onLoaded);
      um.events.removeUserUnloaded(onGone);
      um.events.removeSilentRenewError(onGone);
    };
  }, [um]);

  const signIn = useCallback((returnTo?: string) => um.signinRedirect({ state: returnTo ?? '/' }), [um]);
  const signOut = useCallback(async () => {
    await um.signoutRedirect();
  }, [um]);
  const value = useMemo<AuthState>(() => ({ ...state, signIn, signOut }), [state, signIn, signOut]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
