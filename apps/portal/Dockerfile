FROM node:22-alpine AS build
ARG SOURCE_REPOSITORY=
LABEL org.opencontainers.image.source="${SOURCE_REPOSITORY}"
LABEL org.opencontainers.image.description="SmartPrint customer and shop web portal"
WORKDIR /workspace
RUN corepack enable && corepack prepare pnpm@10.18.0 --activate
COPY . .
RUN pnpm install --frozen-lockfile
ARG VITE_API_BASE_URL=
ARG PUBLIC_BASE_URL=https://smartprint.quantardge.in
ARG VITE_ENABLE_PHOTO_STUDIO_QA=false
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL}
ENV VITE_PUBLIC_BASE_URL=${PUBLIC_BASE_URL}
ENV VITE_ENABLE_PHOTO_STUDIO_QA=${VITE_ENABLE_PHOTO_STUDIO_QA}
RUN pnpm --filter @smart-print/portal build

FROM nginx:alpine AS runtime
COPY apps/portal/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /workspace/apps/portal/dist/ /usr/share/nginx/html/
EXPOSE 80
HEALTHCHECK --interval=20s --timeout=5s --start-period=10s --retries=5 \
    CMD wget -q -O /dev/null http://127.0.0.1/ || exit 1
