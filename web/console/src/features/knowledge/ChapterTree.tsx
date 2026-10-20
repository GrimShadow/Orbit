import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { assetHref, nestChapters, pageRange, type ChapterNode } from './knowledgeModel';
import type { ChapterDto } from './knowledgeTypes';

function Nodes({
  nodes,
  assetId,
  depth,
  label,
}: {
  nodes: ChapterNode<ChapterDto>[];
  assetId: string;
  depth: number;
  label?: string;
}) {
  const { t } = useTranslation();
  return (
    <ul className="dam-chapters" data-depth={depth} aria-label={label}>
      {nodes.map(({ chapter: c, children }, i) => {
        const pages = pageRange(t, c.pageFrom, c.pageTo);
        return (
          <li key={`${i}-${c.title}`} className="dam-chapters__item">
            <span className="dam-chapters__row">
              {c.pageFrom != null ? (
                <Link className="dam-chapters__link" to={assetHref(assetId, c.pageFrom)}>
                  {c.title}
                </Link>
              ) : (
                <span className="dam-chapters__title">{c.title}</span>
              )}
              {pages && <span className="dam-chapters__pages">{pages}</span>}
            </span>
            {children.length > 0 && <Nodes nodes={children} assetId={assetId} depth={depth + 1} />}
          </li>
        );
      })}
    </ul>
  );
}

/** Chapters as a nested list, indented by level. A chapter with a page links to the asset page opened at that page; one without
 * (an HTML heading, or a manual entry with no page) is plain text. */
export function ChapterTree({ chapters, assetId, label }: { chapters: ChapterDto[]; assetId: string; label: string }) {
  const tree = nestChapters(chapters);
  return <Nodes nodes={tree} assetId={assetId} depth={0} label={label} />;
}
