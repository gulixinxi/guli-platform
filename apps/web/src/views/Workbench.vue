<template>
  <section class="workbench">
    <h1>业务工作台</h1>
    <p>选择业务系统，继续处理您有权访问的工作。</p>
    <div class="system-links">
      <router-link v-for="system in availableSystems" :key="system.key" :to="system.route" class="system-link">
        <strong>{{ system.label }}</strong>
        <span>{{ system.description }}</span>
      </router-link>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useAuthStore } from '../stores/auth';
import { canAccessNavigationItem, shellNavigation } from '../layout/navigation';

const auth = useAuthStore();
const availableSystems = computed(() => shellNavigation
  .filter(system => system.key === 'crm' || system.key === 'erp')
  .map(system => {
    const firstPage = system.groups.flatMap(group => group.items)
      .find(item => item.route && canAccessNavigationItem(item, auth.user?.permissionCodes ?? []));
    return firstPage && {
      key: system.key,
      label: system.label,
      route: firstPage.route!,
      description: system.key === 'crm' ? '客户、跟进与商机' : '主数据与业务单据',
    };
  })
  .filter((system): system is NonNullable<typeof system> => Boolean(system)));
</script>

<style scoped>
.workbench { padding: var(--layout-page-pad-y, 24px) var(--layout-page-pad-x, 24px); }
.workbench h1 { margin: 0 0 8px; font-size: 22px; color: var(--text-primary); }
.workbench p { margin: 0 0 24px; color: var(--text-secondary); }
.system-links { display: flex; flex-wrap: wrap; gap: 16px; }
.system-link { display: flex; flex-direction: column; gap: 8px; min-width: 220px; padding: 20px; border: 1px solid var(--border-default); border-radius: 8px; color: var(--text-primary); text-decoration: none; background: var(--surface-default, #fff); }
.system-link:hover, .system-link:focus-visible { border-color: var(--primary, #0a6ed1); }
.system-link span { color: var(--text-secondary); }
</style>
