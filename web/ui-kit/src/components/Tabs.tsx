import { useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';

export interface TabItem {
  id: string;
  label: string;
  content: ReactNode;
}

export function Tabs({ tabs, initialId, label }: { tabs: TabItem[]; initialId?: string; label: string }) {
  const base = useId();
  const [active, setActive] = useState(initialId ?? tabs[0]?.id);
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});

  const move = (i: number) => {
    const t = tabs[(i + tabs.length) % tabs.length];
    if (!t) return;
    setActive(t.id);
    refs.current[t.id]?.focus();
  };
  const onKey = (e: KeyboardEvent, i: number) => {
    const map: Record<string, number> = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: tabs.length - 1 };
    if (e.key in map) {
      e.preventDefault();
      move(map[e.key]!);
    }
  };

  return (
    <div>
      <div role="tablist" aria-label={label} className="dam-tablist">
        {tabs.map((t, i) => (
          <button
            key={t.id}
            ref={(el) => {
              refs.current[t.id] = el;
            }}
            role="tab"
            type="button"
            className="dam-tab"
            id={`${base}-tab-${t.id}`}
            aria-controls={`${base}-panel-${t.id}`}
            aria-selected={active === t.id}
            tabIndex={active === t.id ? 0 : -1}
            onClick={() => setActive(t.id)}
            onKeyDown={(e) => onKey(e, i)}
          >
            {t.label}
          </button>
        ))}
      </div>
      {tabs.map((t) => (
        <div
          key={t.id}
          role="tabpanel"
          className="dam-tabpanel"
          id={`${base}-panel-${t.id}`}
          aria-labelledby={`${base}-tab-${t.id}`}
          hidden={active !== t.id}
          tabIndex={0}
        >
          {active === t.id && t.content}
        </div>
      ))}
    </div>
  );
}
