#!/bin/sh
# RabbitMQ 初始化：创建 /business vhost 并授权
# 在 RabbitMQ 启动后由 docker-compose command 调用

set -e

# 等待 RabbitMQ 就绪
echo "Waiting for RabbitMQ to start..."
until rabbitmq-diagnostics check_port_connectivity 2>/dev/null; do
    sleep 2
done

echo "Creating /business vhost..."
rabbitmqctl add_vhost /business 2>/dev/null || echo "VHost /business already exists"

echo "Setting permissions for guest on /business..."
rabbitmqctl set_permissions -p /business guest ".*" ".*" ".*"

echo "RabbitMQ /business vhost initialized."
