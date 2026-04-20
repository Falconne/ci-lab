<template>
  <div class="text-center pa-12">
    <v-icon icon="mdi-source-merge" size="80" color="primary" class="mb-6" />
    <h1 class="text-h3 mb-4">Welcome to Mergician</h1>
    <p class="text-body-1 text-grey-darken-1 mb-8" style="max-width: 500px; margin: 0 auto;">
      Mergician helps you coordinate merge requests across multiple Git repositories.
      Sign in with your GitLab account to get started.
    </p>
    <v-btn
      color="primary"
      size="large"
      href="/api/auth/login"
      prepend-icon="mdi-login"
    >
      Sign in with GitLab
    </v-btn>
  </div>
</template>
