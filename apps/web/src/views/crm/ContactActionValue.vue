<template>
  <span v-if="value" class="contact-action-value" :title="value">
    <a v-if="kind === 'email' && emailHref" :href="emailHref" class="contact-value" title="打开邮件应用">{{ value }}</a>
    <a v-else-if="kind === 'phone' && isMobileDevice && phoneHref" :href="phoneHref" class="contact-value" title="拨打电话">{{ value }}</a>
    <button v-else type="button" class="contact-value contact-copy" :title="`复制${label}`" @click="copyValue">{{ value }}</button>
    <el-button link size="small" class="copy-button" :aria-label="`复制${label}`" @click="copyValue">复制</el-button>
  </span>
  <span v-else>-</span>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { ElMessage } from 'element-plus';

const props = defineProps<{ value?: string | null; kind: 'phone' | 'wechat' | 'email'; label: string }>();
const isMobileDevice = ref(false);
let mobileQuery: MediaQueryList | undefined;

function updateMobile() { isMobileDevice.value = mobileQuery?.matches ?? false; }
onMounted(() => {
  mobileQuery = window.matchMedia('(max-width: 767px) and (pointer: coarse)');
  updateMobile();
  mobileQuery.addEventListener('change', updateMobile);
});
onUnmounted(() => mobileQuery?.removeEventListener('change', updateMobile));

const phoneHref = computed(() => {
  const number = props.value?.trim() || '';
  return /^[+\d][\d\s()\-]{2,29}$/.test(number) ? `tel:${number.replace(/[\s()\-]/g, '')}` : '';
});
const emailHref = computed(() => {
  const email = props.value?.trim() || '';
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) ? `mailto:${email}` : '';
});

async function copyValue() {
  if (!props.value) return;
  try {
    await navigator.clipboard.writeText(props.value);
    ElMessage.success(`${props.label}已复制`);
  } catch {
    ElMessage.error('复制失败，请手动选择文字复制');
  }
}
</script>

<style scoped>
.contact-action-value { display: inline-flex; align-items: center; gap: 4px; max-width: 100%; white-space: nowrap; }
.contact-value { display: inline-block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--el-color-primary); max-width: 100%; text-decoration: none; }
.contact-copy { padding: 0; border: 0; background: none; font: inherit; cursor: pointer; text-align: left; }
.contact-value:hover { text-decoration: underline; }
.copy-button { flex: none; }
</style>
