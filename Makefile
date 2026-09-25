export DAM_MIGRATION_CONNECTION ?= Host=localhost;Port=15432;Database=dam;Username=dam;Password=dam
.PHONY: migrate build test up up-search up-scan up-obs up-all down reset
build: ; dotnet build Dam.sln && pnpm -r build
test:  ; dotnet test Dam.sln && pnpm test
up:    ; docker compose -f deploy/docker-compose.yml up -d --wait
up-search: ; docker compose -f deploy/docker-compose.yml --profile search up -d --wait
up-scan: ; docker compose -f deploy/docker-compose.yml --profile scan up -d --wait
up-obs: ; docker compose -f deploy/docker-compose.yml --profile obs up -d
up-all: ; docker compose -f deploy/docker-compose.yml --profile all up -d --wait
down:  ; docker compose -f deploy/docker-compose.yml --profile all down
reset: ; docker compose -f deploy/docker-compose.yml --profile all down -v && $(MAKE) up
migrate:
	dotnet ef database update -p src/Dam.Infrastructure -s src/Dam.Infrastructure
	docker compose -f deploy/docker-compose.yml exec -T postgres psql -U dam -d dam -c "GRANT dam_app TO dam_app_login"
