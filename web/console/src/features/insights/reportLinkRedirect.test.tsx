import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { ReportLinkRedirect } from './ReportLinkRedirect';

function Where() {
  const { pathname, search } = useLocation();
  return <p>{pathname + search}</p>;
}

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/insights/reports/:name" element={<ReportLinkRedirect />} />
        <Route path="/insights/reports" element={<Where />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('the link in a scheduled report email', () => {
  it('opens the Reports page on that report and period', () => {
    renderAt('/insights/reports/workflow-sla?days=90');
    expect(screen.getByText('/insights/reports?report=workflow-sla&days=90')).toBeInTheDocument();
  });

  it('works without a period', () => {
    renderAt('/insights/reports/kpis');
    expect(screen.getByText('/insights/reports?report=kpis')).toBeInTheDocument();
  });
});
