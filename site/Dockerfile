FROM node:22-alpine
WORKDIR /app
COPY server.mjs terrain.mjs saves.mjs archive.mjs world.mjs hidden.mjs base-areas.json ./
COPY public ./public
USER node
ENV PORT=8080 DATA_DIR=/app/data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
CMD ["node", "server.mjs"]
