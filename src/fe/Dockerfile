FROM node:22-alpine AS build
WORKDIR /app

# Copy package files and install dependencies
COPY package.json package-lock.json* ./
RUN npm ci 2>/dev/null || npm install

# Copy source and build
COPY . .
RUN npm run build

# Serve with nginx
FROM nginx:1.27-alpine

# Create non-root user
RUN addgroup -g 1000 mergician && adduser -u 1000 -G mergician -D mergician

# Configure nginx to run as non-root
RUN sed -i '/^user  nginx;/d' /etc/nginx/nginx.conf && \
    sed -i 's|/var/run/nginx.pid|/tmp/nginx.pid|g' /etc/nginx/nginx.conf && \
    chown -R mergician:mergician /var/cache/nginx /var/log/nginx /etc/nginx/conf.d

COPY --from=build --chown=mergician:mergician /app/dist /usr/share/nginx/html
COPY --chown=mergician:mergician nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 8080

USER mergician
