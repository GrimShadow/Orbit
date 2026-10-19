import { render, screen } from '@testing-library/react';
import { axe } from 'vitest-axe';
import { BarChart, DistributionBar, Sparkline, StatCard, TrendBadge } from './index';

describe('charts', () => {
  it('BarChart exposes one accessible summary and labels first, last and every Nth bar', () => {
    const data = Array.from({ length: 10 }, (_, i) => ({ label: `D${i}`, value: i }));
    const { container } = render(<BarChart data={data} label="Downloads per day" labelEvery={4} />);
    expect(screen.getByRole('img', { name: 'Downloads per day' })).toBeInTheDocument();
    const ticks = [...container.querySelectorAll('.dam-bars__tick')].map((t) => t.textContent);
    expect(ticks.filter(Boolean)).toEqual(['D0', 'D4', 'D8', 'D9']);
    expect(container.querySelectorAll('.dam-bars__bar--zero')).toHaveLength(1);
  });

  it('DistributionBar lists every segment with its share and hides empty ones from the bar', async () => {
    const { container } = render(
      <DistributionBar
        label="By status"
        segments={[
          { label: 'Published', value: 6 },
          { label: 'Draft', value: 2 },
          { label: 'Archived', value: 0 },
        ]}
      />,
    );
    expect(screen.getByRole('group', { name: 'By status' })).toBeInTheDocument();
    expect(screen.getByText('75%')).toBeInTheDocument();
    expect(container.querySelectorAll('.dam-dist__bar .dam-dist__seg')).toHaveLength(2);
    expect(await axe(container)).toHaveNoViolations();
  });

  it('TrendBadge says which way it moved, and whether that is good, in words', () => {
    const { rerender } = render(<TrendBadge change={12.4} suffix="vs the previous 30 days" />);
    expect(screen.getByText('up 12% vs the previous 30 days')).toBeInTheDocument();
    expect(document.querySelector('.dam-trend--good')).not.toBeNull();
    rerender(<TrendBadge change={12.4} goodWhen="down" suffix="vs before" />);
    expect(document.querySelector('.dam-trend--bad')).not.toBeNull();
    rerender(<TrendBadge change={null} suffix="x" emptyLabel="n/a" />);
    expect(screen.getByText('n/a')).toBeInTheDocument();
  });

  it('Sparkline needs two points and is decorative unless labelled; StatCard hosts trend and chart', async () => {
    const { container, rerender } = render(<Sparkline values={[1]} />);
    expect(container.querySelector('svg')).toBeNull();
    rerender(
      <StatCard label="Usage" value="42" trend={<TrendBadge change={5} suffix="s" />} chart={<Sparkline values={[1, 3, 2]} />} />,
    );
    expect(container.querySelector('.dam-stat__trend')).not.toBeNull();
    expect(container.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');
    expect(await axe(container)).toHaveNoViolations();
  });
});
