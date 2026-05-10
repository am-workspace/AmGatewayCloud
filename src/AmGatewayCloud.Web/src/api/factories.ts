import client from './client'
import type { FactoryNode } from '@/types'

/** 查询工厂/车间树 */
export function getFactoryTree() {
  return client.get<FactoryNode[]>('/api/factories/tree')
}
