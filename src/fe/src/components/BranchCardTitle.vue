<template>
  <div v-if="item.mergeRequestTitle" class="branch-card-title branch-card-title--with-mr">
    <div v-if="item.mergeRequestUrl" class="mr-title-row">
      <a
        class="mr-title-link"
        :href="item.mergeRequestUrl"
        target="_blank"
        rel="noopener noreferrer"
      >{{ item.mergeRequestTitle }}</a>
      <a
        :href="item.mergeRequestUrl"
        target="_blank"
        rel="noopener noreferrer"
        class="mr-external-link-btn"
        aria-label="Open merge request"
      ><v-icon size="14" class="mr-external-link-icon">mdi-open-in-new</v-icon></a>
    </div>
    <span v-else class="mr-title-text">{{ item.mergeRequestTitle }}</span>
    <div class="branch-subtitle-row">
      <a
        v-if="item.projectUrl"
        class="branch-subtitle-link"
        :href="branchUrl"
        target="_blank"
        rel="noopener noreferrer"
      >{{ item.projectName }} | {{ item.branchName }}</a>
      <span v-else class="branch-subtitle-text">{{ item.projectName }} | {{ item.branchName }}</span>
      <v-tooltip text="Copy branch name" location="top">
        <template #activator="{ props: tooltipProps }">
          <v-btn
            v-bind="tooltipProps"
            icon
            size="x-small"
            variant="text"
            color="grey"
            class="copy-branch-btn"
            aria-label="Copy branch name"
            @click.stop="copyBranchName"
          >
            <v-icon size="16">mdi-content-copy</v-icon>
          </v-btn>
        </template>
      </v-tooltip>
    </div>
  </div>
  <div v-else class="branch-card-title">
    <a
      v-if="item.projectUrl"
      class="branch-title-link"
      :href="branchUrl"
      target="_blank"
      rel="noopener noreferrer"
    >
      {{ item.projectName }} | {{ item.branchName }}
    </a>
    <span v-else class="branch-title-text">{{ item.projectName }} | {{ item.branchName }}</span>
    <v-tooltip text="Copy branch name" location="top">
      <template #activator="{ props: tooltipProps }">
        <v-btn
          v-bind="tooltipProps"
          icon
          size="x-small"
          variant="text"
          color="grey"
          class="copy-branch-btn"
          aria-label="Copy branch name"
          @click.stop="copyBranchName"
        >
          <v-icon size="16">mdi-content-copy</v-icon>
        </v-btn>
      </template>
    </v-tooltip>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { BranchWithActivity } from '@/types/mergeGroup'

const props = defineProps<{
  item: BranchWithActivity
}>()

const branchUrl = computed<string>(() => {
  if (!props.item.projectUrl) return ''
  return `${props.item.projectUrl}/-/tree/${encodeURIComponent(props.item.branchName)}?ref_type=heads`
})

async function copyBranchName() {
  try {
    await navigator.clipboard.writeText(props.item.branchName)
  } catch (err) {
    console.warn('[Mergician] Failed to copy branch name to clipboard:', err)
  }
}
</script>
