.PHONY: build test up down reset
build: ; dotnet build Dam.sln && pnpm -r build
test:  ; dotnet test Dam.sln && pnpm test
up:    ; docker compose -f deploy/docker-compose.yml up -d --wait
down:  ; docker compose -f deploy/docker-compose.yml down
reset: ; docker compose -f deploy/docker-compose.yml down -v && $(MAKE) up
