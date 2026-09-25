# Build from the repo root: docker build -f web/console/Dockerfile .
FROM node:22-alpine AS build
RUN corepack enable
WORKDIR /repo
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY web/console/package.json web/console/
COPY web/ui-kit/package.json web/ui-kit/
COPY web/sdk-js/package.json web/sdk-js/
RUN pnpm install --frozen-lockfile
COPY web ./web
RUN pnpm --filter @dam/console build

FROM nginxinc/nginx-unprivileged:1.27-alpine
# Pull OS security fixes that the pinned base image lags behind on (needs root; the image drops to uid 101 below).
USER root
RUN apk upgrade --no-cache
COPY web/console/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /repo/web/console/dist /usr/share/nginx/html
USER 101
EXPOSE 8080
