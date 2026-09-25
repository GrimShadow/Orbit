CREATE DATABASE keycloak OWNER dam;
-- Runtime login for the app. NOT a superuser, so Postgres RLS applies. Migrations run as `dam` (owner).
-- The migration creates/grants the dam_app group role; this login joins it after the first migrate.
CREATE ROLE dam_app_login LOGIN PASSWORD 'dam_app' NOSUPERUSER NOBYPASSRLS;
-- Outbox relay login: may only read/mark outbox rows across tenants (see SecuritySql.SystemRelayRole).
CREATE ROLE dam_system_login LOGIN PASSWORD 'dam_system' NOSUPERUSER NOBYPASSRLS;
