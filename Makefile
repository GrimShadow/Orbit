export DAM_MIGRATION_CONNECTION ?= Host=localhost;Port=15432;Database=dam;Username=dam;Password=dam
.PHONY: run-worker run-scheduler run-delivery run-api openapi migrate build test test-ai up up-search up-scan up-mail up-ai up-obs up-all down reset demo-up demo-down demo-status
build: ; dotnet build Dam.sln && pnpm -r build
test:  ; dotnet test Dam.sln && pnpm test
up:    ; docker compose -f deploy/docker-compose.yml up -d --wait
up-search: ; docker compose -f deploy/docker-compose.yml --profile search up -d --wait
up-scan: ; docker compose -f deploy/docker-compose.yml --profile scan up -d --wait
up-mail: ; docker compose -f deploy/docker-compose.yml --profile mail up -d --wait
up-ai: ; docker compose -f deploy/docker-compose.yml --profile ai up -d --build --wait
up-obs: ; docker compose -f deploy/docker-compose.yml --profile obs up -d
test-ai: ; docker compose -f deploy/docker-compose.yml run --rm --build ai-sidecar-tests
up-all: ; docker compose -f deploy/docker-compose.yml --profile all up -d --wait
down:  ; docker compose -f deploy/docker-compose.yml --profile all down
reset: ; docker compose -f deploy/docker-compose.yml --profile all down -v && $(MAKE) up
migrate:
	dotnet ef database update -p src/Dam.Infrastructure -s src/Dam.Infrastructure
	docker compose -f deploy/docker-compose.yml exec -T postgres psql -U dam -d dam -c "GRANT dam_app TO dam_app_login; GRANT dam_system TO dam_system_login"
run-api:
	cd src/Dam.Api && dotnet run
openapi:
	UPDATE_OPENAPI=1 dotnet test tests/Dam.IntegrationTests --filter openapi
	pnpm --filter @dam/sdk-js generate
run-worker:
	cd src/Dam.Worker && dotnet run
run-scheduler:
	cd src/Dam.Scheduler && dotnet run
run-delivery:
	cd src/Dam.Delivery && dotnet run
# Share a demo from this Mac (deploy/demo/README.md). USERS="anna.reviewer,ben.reviewer:Editor"; optional LOCAL=1 RESET_PASSWORDS=1 RESEED=1 / PURGE=1.
demo-up:
	deploy/demo/demo.sh up $(if $(USERS),--users "$(USERS)") $(if $(LOCAL),--local) $(if $(RESET_PASSWORDS),--reset-passwords) $(if $(RESEED),--reseed)
demo-down:
	deploy/demo/demo.sh down $(if $(PURGE),--purge)
demo-status:
	deploy/demo/demo.sh status
