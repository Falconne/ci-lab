{{/*
Expand the name of the chart.
*/}}
{{- define "mergician.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
We truncate at 63 chars because some Kubernetes name fields are limited to this.
*/}}
{{- define "mergician.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart label.
*/}}
{{- define "mergician.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "mergician.labels" -}}
helm.sh/chart: {{ include "mergician.chart" . }}
{{ include "mergician.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "mergician.selectorLabels" -}}
app.kubernetes.io/name: {{ include "mergician.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Service account name
*/}}
{{- define "mergician.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "mergician.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Name of the Mergician credentials secret.
When secrets.existingSecret is set, that secret is used instead of the chart-managed one.
The secret must contain: gitlab-service-token, gitlab-oauth-client-id,
gitlab-oauth-client-secret, and (when postgresql.enabled=false) database-password.
*/}}
{{- define "mergician.secretName" -}}
{{- if .Values.secrets.existingSecret }}
{{- .Values.secrets.existingSecret }}
{{- else }}
{{- printf "%s-secrets" (include "mergician.fullname" .) }}
{{- end }}
{{- end }}

{{/*
PostgreSQL service hostname.
When postgresql.enabled, uses the Bitnami subchart service name.
Otherwise uses externalDatabase.host.
*/}}
{{- define "mergician.databaseHost" -}}
{{- if .Values.postgresql.enabled }}
{{- printf "%s-postgresql" .Release.Name }}
{{- else }}
{{- required "externalDatabase.host is required when postgresql.enabled is false" .Values.externalDatabase.host }}
{{- end }}
{{- end }}

{{/*
PostgreSQL port.
*/}}
{{- define "mergician.databasePort" -}}
{{- if .Values.postgresql.enabled }}
{{- "5432" }}
{{- else }}
{{- .Values.externalDatabase.port | toString }}
{{- end }}
{{- end }}

{{/*
PostgreSQL database name.
*/}}
{{- define "mergician.databaseName" -}}
{{- if .Values.postgresql.enabled }}
{{- .Values.postgresql.auth.database }}
{{- else }}
{{- .Values.externalDatabase.database }}
{{- end }}
{{- end }}

{{/*
PostgreSQL username.
*/}}
{{- define "mergician.databaseUsername" -}}
{{- if .Values.postgresql.enabled }}
{{- .Values.postgresql.auth.username }}
{{- else }}
{{- .Values.externalDatabase.username }}
{{- end }}
{{- end }}

{{/*
Name of the secret containing the database password.
When postgresql.enabled, this is the Bitnami subchart secret.
Otherwise, the password lives in the Mergician credentials secret.
*/}}
{{- define "mergician.databaseSecretName" -}}
{{- if .Values.postgresql.enabled }}
{{- if .Values.postgresql.auth.existingSecret }}
{{- .Values.postgresql.auth.existingSecret }}
{{- else }}
{{- printf "%s-postgresql" .Release.Name }}
{{- end }}
{{- else }}
{{- include "mergician.secretName" . }}
{{- end }}
{{- end }}

{{/*
Key within the database password secret that holds the password.
Bitnami PostgreSQL v14+ uses "password" for the custom user.
External database password is stored under "database-password" in the Mergician secret.
*/}}
{{- define "mergician.databasePasswordKey" -}}
{{- if .Values.postgresql.enabled }}
{{- .Values.postgresql.auth.secretKeys.userPasswordKey | default "password" }}
{{- else }}
{{- "database-password" }}
{{- end }}
{{- end }}
