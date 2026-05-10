# ── 构建阶段 ──
FROM node:20-alpine AS build
WORKDIR /app

# 先复制依赖描述，利用 Docker 缓存
COPY src/AmGatewayCloud.Web/package.json src/AmGatewayCloud.Web/package-lock.json* ./

# 安装依赖
RUN npm ci --prefer-offline

# 复制源码并构建
COPY src/AmGatewayCloud.Web/ ./
RUN npm run build

# ── 运行阶段 ──
FROM nginx:alpine

# 复制自定义 nginx 配置
COPY docker/web/nginx.conf /etc/nginx/conf.d/default.conf

# 复制构建产物
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
