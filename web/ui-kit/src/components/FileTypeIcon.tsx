import { BookOpen, Cube, File, FileHtml, FileText, Image, SpeakerHigh, VideoCamera, type IconProps } from '@phosphor-icons/react';
import type { ComponentType } from 'react';

/** The asset-type vocabulary is a fixed technical taxonomy (spec Step 1.3's `AssetType`), not tenant-specific content,
 * so this mapping lives in the shared design system rather than being duplicated per console screen. */
const ICONS: Record<string, ComponentType<IconProps>> = {
  image: Image,
  video: VideoCamera,
  audio: SpeakerHigh,
  document: FileText,
  manual: BookOpen,
  '3d': Cube,
  html5: FileHtml,
};

export function FileTypeIcon({ type, ...rest }: { type: string } & IconProps) {
  const Icon = ICONS[type] ?? File;
  return <Icon {...rest} />;
}
