export interface FacetOption {
  value: string;
  label: string;
  count?: number;
}
export interface Facet {
  id: string;
  label: string;
  options: FacetOption[];
}

interface FacetListProps {
  facets: Facet[];
  selected: Record<string, string[]>;
  onChange: (selected: Record<string, string[]>) => void;
}

export function FacetList({ facets, selected, onChange }: FacetListProps) {
  const toggle = (facetId: string, value: string, on: boolean) => {
    const cur = selected[facetId] ?? [];
    onChange({ ...selected, [facetId]: on ? [...cur, value] : cur.filter((v) => v !== value) });
  };
  return (
    <div>
      {facets.map((f) => (
        <fieldset key={f.id} className="dam-facet">
          <legend>{f.label}</legend>
          {f.options.map((o) => (
            <label key={o.value} className="dam-facet__opt">
              <input
                type="checkbox"
                checked={(selected[f.id] ?? []).includes(o.value)}
                onChange={(e) => toggle(f.id, o.value, e.target.checked)}
              />
              <span>{o.label}</span>
              {o.count !== undefined && <span className="dam-facet__count">{o.count}</span>}
            </label>
          ))}
        </fieldset>
      ))}
    </div>
  );
}
