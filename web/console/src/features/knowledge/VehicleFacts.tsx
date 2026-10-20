import { useTranslation } from 'react-i18next';
import { Badge } from '../../ui';
import type { VehicleDto } from './knowledgeTypes';

/** The vehicle a VIN resolved to - model, variant, model year, theme - as a definition list. */
export function VehicleFacts({ vehicle }: { vehicle: VehicleDto }) {
  const { t } = useTranslation();
  const dash = <span className="dam-hint">{t('knowledge.none')}</span>;
  return (
    <dl className="dam-facts dam-knvehicle">
      <div className="dam-facts__row">
        <dt>{t('knowledge.field.model')}</dt>
        <dd className="dam-kncode">{vehicle.model}</dd>
      </div>
      <div className="dam-facts__row">
        <dt>{t('knowledge.field.variant')}</dt>
        <dd className="dam-kncode">{vehicle.variant ?? dash}</dd>
      </div>
      <div className="dam-facts__row">
        <dt>{t('knowledge.result.modelYear')}</dt>
        <dd>{vehicle.modelYear ?? dash}</dd>
      </div>
      <div className="dam-facts__row">
        <dt>{t('knowledge.field.theme')}</dt>
        <dd>
          {vehicle.theme ? (
            <Badge size="sm" tone="brand">
              {vehicle.theme}
            </Badge>
          ) : (
            dash
          )}
        </dd>
      </div>
    </dl>
  );
}
