# Mergician Kubernetes Deployment

This directory contains a Helm chart for deploying Mergician to Kubernetes,
along with a sample ArgoCD Application manifest.

## Directory Structure

```
k8s/
├── Chart.yaml            # Chart metadata; Bitnami PostgreSQL listed as dependency
├── values.yaml           # Default values — safe to commit (no secrets)
├── argocd-app.yaml       # Sample ArgoCD Application manifest
├── templates/
│   ├── _helpers.tpl      # Template helpers
│   ├── deployment.yaml   # Mergician Deployment
│   ├── service.yaml      # ClusterIP Service
│   ├── ingress.yaml      # Ingress resource
│   ├── configmap.yaml    # Non-sensitive configuration
│   ├── secret.yaml       # Chart-managed Secret (dev/test only)
│   ├── serviceaccount.yaml
│   ├── hpa.yaml          # HorizontalPodAutoscaler
│   ├── pdb.yaml          # PodDisruptionBudget
│   └── NOTES.txt         # Post-install output
└── README.md             # This file
```

## Prerequisites

- Kubernetes 1.24+
- Helm 3.10+
- An NGINX (or similar) Ingress controller
- cert-manager (recommended for TLS)
- A GitLab instance accessible from the cluster

## Quick Start

### 1. Fetch chart dependencies

```bash
helm dependency update k8s/
```

### 2. Configure GitLab OAuth

Before deploying, create an OAuth application in GitLab:

1. Go to **GitLab → User Settings → Applications** (or the group/admin equivalent).
2. Set the **Redirect URI** to `https://<your-domain>/api/auth/callback`.
3. Enable scopes: **api**, **read_user**.
4. Note the **Application ID** (Client ID) and **Secret**.

### 3. Create a GitLab service token

Create a GitLab personal or project access token for the Mergician service
account with the **api** scope. This token is used for background operations
(pipeline status, merge request management, etc.).

### 4. Provide secrets

Choose one of the approaches below.

---

## Secret Management

### Option A — External Secrets Operator (recommended for production)

[External Secrets Operator](https://external-secrets.io) syncs secrets from
an external store (AWS Secrets Manager, HashiCorp Vault, Azure Key Vault, etc.)
into Kubernetes Secrets.

```yaml
# external-secret.yaml
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: mergician-credentials
  namespace: mergician
spec:
  refreshInterval: 1h
  secretStoreRef:
    name: my-cluster-secret-store   # your SecretStore name
    kind: ClusterSecretStore
  target:
    name: mergician-credentials     # the K8s Secret name
    creationPolicy: Owner
  data:
    - secretKey: gitlab-service-token
      remoteRef:
        key: mergician/gitlab-service-token    # path in your secret store
    - secretKey: gitlab-oauth-client-id
      remoteRef:
        key: mergician/gitlab-oauth-client-id
    - secretKey: gitlab-oauth-client-secret
      remoteRef:
        key: mergician/gitlab-oauth-client-secret
---
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: mergician-db-credentials
  namespace: mergician
spec:
  refreshInterval: 1h
  secretStoreRef:
    name: my-cluster-secret-store
    kind: ClusterSecretStore
  target:
    name: mergician-db-credentials
    creationPolicy: Owner
  data:
    - secretKey: password                      # key expected by Bitnami PostgreSQL chart
      remoteRef:
        key: mergician/database-password
```

Then reference the secrets in your values:

```yaml
# values-production.yaml
secrets:
  existingSecret: mergician-credentials

postgresql:
  auth:
    existingSecret: mergician-db-credentials
```

---

### Option B — Sealed Secrets

[Sealed Secrets](https://github.com/bitnami-labs/sealed-secrets) encrypts
secrets in Git using a cluster-specific key. Safe to commit to your repository.

```bash
# Install the kubeseal CLI, then:

kubectl create secret generic mergician-credentials \
  --namespace mergician \
  --from-literal=gitlab-service-token='glpat-xxxxxxxxxxxxxxxxxxxx' \
  --from-literal=gitlab-oauth-client-id='your-oauth-client-id' \
  --from-literal=gitlab-oauth-client-secret='your-oauth-client-secret' \
  --dry-run=client -o yaml \
  | kubeseal --controller-namespace sealed-secrets \
             --controller-name sealed-secrets-controller \
             --format yaml > k8s/sealed-secret-mergician-credentials.yaml

# For the PostgreSQL password:
kubectl create secret generic mergician-db-credentials \
  --namespace mergician \
  --from-literal=password='your-db-password' \
  --dry-run=client -o yaml \
  | kubeseal ... > k8s/sealed-secret-mergician-db.yaml
```

Then reference the secrets in your values:

```yaml
# values-production.yaml
secrets:
  existingSecret: mergician-credentials

postgresql:
  auth:
    existingSecret: mergician-db-credentials
```

---

### Option C — HashiCorp Vault with ArgoCD Vault Plugin

If your company uses Vault with the
[ArgoCD Vault Plugin](https://argocd-vault-plugin.readthedocs.io/), create
a dedicated Vault path for Mergician secrets and configure the plugin to
inject them at sync time.

---

### Option D — Inline values (dev / local testing ONLY)

**Never use this in production or commit real credentials to Git.**

```bash
helm install mergician k8s/ \
  --namespace mergician --create-namespace \
  --set config.baseUrl=https://mergician.example.com \
  --set config.gitlab.url=https://gitlab.example.com \
  --set secrets.gitlabServiceToken=glpat-xxxx \
  --set secrets.gitlabOauthClientId=your-client-id \
  --set secrets.gitlabOauthClientSecret=your-client-secret \
  --set postgresql.auth.password=strong-password-here
```

---

## Deploying with ArgoCD

1. Edit `k8s/argocd-app.yaml` — update `repoURL`, `targetRevision`,
   destination `namespace`, and Helm value overrides.
2. Create a production values file (e.g. `k8s/values-production.yaml`) with
   non-sensitive overrides (image tag, domain, resource limits, etc.).
3. Apply the application:
   ```bash
   kubectl apply -f k8s/argocd-app.yaml
   ```
4. Watch the sync:
   ```bash
   argocd app sync mergician
   argocd app wait mergician --health
   ```

---

## Using an External Database

To use a managed PostgreSQL (AWS RDS, Google Cloud SQL, Azure Database, etc.)
instead of the built-in Bitnami subchart:

```yaml
# values-production.yaml
postgresql:
  enabled: false

externalDatabase:
  host: my-postgres.cluster.example.com
  port: 5432
  database: mergician
  username: mergician

config:
  database:
    sslMode: "VerifyFull"   # Recommended for managed databases

secrets:
  existingSecret: mergician-credentials  # Must include database-password key
```

---

## Configuration Reference

| Values key                        | Description                                                  | Default                    |
|-----------------------------------|--------------------------------------------------------------|----------------------------|
| `image.repository`                | Container image registry/name                                | `your-registry.../mergician`|
| `image.tag`                       | Image tag (defaults to chart `appVersion`)                   | `""`                       |
| `replicaCount`                    | Number of Mergician replicas                                 | `2`                        |
| `config.baseUrl`                  | Public URL — must match OAuth callback base                  | `https://mergician.example.com` |
| `config.gitlab.url`               | GitLab public URL (browser-facing)                           | `https://gitlab.example.com` |
| `config.gitlab.internalUrl`       | GitLab server-side URL (leave empty to reuse `url`)          | `""`                       |
| `config.gitlab.allowInsecureSsl`  | Disable TLS validation (dev/internal only)                   | `false`                    |
| `config.database.sslMode`         | Npgsql SSL mode for managed databases                        | `""`                       |
| `secrets.existingSecret`          | Name of pre-created credentials Secret (production)         | `""`                       |
| `postgresql.enabled`              | Deploy Bitnami PostgreSQL subchart                           | `true`                     |
| `postgresql.primary.persistence.size` | PVC size for PostgreSQL data                            | `10Gi`                     |
| `ingress.className`               | Ingress class name                                           | `nginx`                    |
| `autoscaling.enabled`             | Enable HPA                                                   | `false`                    |

---

## Upgrading

```bash
helm dependency update k8s/
helm upgrade mergician k8s/ --namespace mergician -f k8s/values-production.yaml
```

Mergician applies database migrations automatically on startup, so no manual
migration step is required.

---

## Beginner's Guide: Docker Compose → Kubernetes + Helm

This section is for engineers who are comfortable with Docker and
`docker compose` but are new to Kubernetes and Helm. It maps the concepts
you already know to the new ones you will encounter when deploying Mergician
to a cluster.

---

### The big picture

With `docker compose`, you describe services, networks, and volumes in a
single YAML file and `docker compose up` creates everything on one machine.

Kubernetes works the same way conceptually, but instead of one machine it
manages a **cluster** of machines (called **nodes**). You still write YAML
describing what you want to run, but you hand it to the Kubernetes API
and the cluster figures out *which* node to put things on, how to restart
them if they crash, how to scale them, and much more.

**Helm** is a package manager for Kubernetes — think of it like `apt` or
`npm`, but for Kubernetes YAML. Rather than writing raw Kubernetes YAML
by hand (which is verbose and error-prone), you use a Helm **chart**: a
folder of templates and a `values.yaml` file. Helm fills in the templates
and submits the final YAML to Kubernetes on your behalf.

---

### Docker Compose concepts mapped to Kubernetes

| Docker Compose concept | Kubernetes equivalent | What it does |
|---|---|---|
| `services:` block | **Deployment** | Declares a containerised workload to run |
| `replicas:` | `spec.replicas` in Deployment | How many copies of the container to run |
| `volumes:` (named volume) | **PersistentVolumeClaim (PVC)** | A durable disk that survives pod restarts |
| `networks:` | **Service** | Stable DNS name and IP that routes traffic to pods |
| Port mapping `"80:5000"` | **Ingress** + **Service** | Routes external traffic into the cluster |
| `environment:` key/value pairs | **ConfigMap** (plain values) + **Secret** (sensitive values) | Injects config into containers as env vars |
| `healthcheck:` | `livenessProbe` / `readinessProbe` | Tells Kubernetes when a container is healthy |
| `depends_on:` | `initContainers` or `startupProbe` | Controls ordering (Kubernetes is less rigid, but probes manage readiness) |
| `.env` file | Kubernetes **Secret** + `secretKeyRef` | Injects credentials without baking them into images |
| `restart: unless-stopped` | Kubernetes default — always restarts | Kubernetes restarts failed pods automatically |
| `image: postgres:17` | Bitnami PostgreSQL **StatefulSet** (via this chart) | A database deployment with ordered startup and stable hostnames |

---

### Key Kubernetes objects used in this chart

#### Deployment
The equivalent of a `docker compose` service entry. It tells Kubernetes
to run N identical copies (pods) of the Mergician container and to replace
them automatically if they crash or the image is updated.

```yaml
# docker-compose equivalent:
services:
  mergician:
    image: my-registry/mergician:1.0
    replicas: 2
```

```yaml
# Kubernetes equivalent (simplified):
kind: Deployment
spec:
  replicas: 2
  template:
    spec:
      containers:
        - image: my-registry/mergician:1.0
```

#### Pod
The smallest unit in Kubernetes — one running instance of your container
(or containers, if you need sidecars). A Deployment manages a set of pods.
You rarely interact with pods directly; you work at the Deployment level.

#### Service
In Docker Compose, containers on the same network reach each other by
service name (`postgres`, `mergician`, etc.). Kubernetes works the same
way, but the stable DNS name is provided by a **Service** object.

A **ClusterIP** Service is internal-only (like being on a Docker bridge
network). An **Ingress** (see below) exposes it externally.

```
Browser → Ingress (port 443) → Service (port 80) → Pod (port 5000)
```

#### Ingress
The equivalent of `ports: "443:5000"` in docker compose, but for a
cluster. An Ingress controller (typically NGINX) runs in the cluster and
acts as a reverse proxy. You declare routing rules in an Ingress resource
and the controller handles TLS termination and forwards traffic to the
right Service.

#### ConfigMap
A place to store plain-text configuration that is injected into containers
as environment variables. Equivalent to the non-secret lines in your
`.env` file. ConfigMaps are safe to commit to Git.

#### Secret
Like a ConfigMap, but the values are base64-encoded and access-controlled.
This is where credentials go — GitLab tokens, OAuth secrets, database
passwords. Never put real Secret values in `values.yaml` and commit them.
See the **Secret Management** section above for production-safe approaches.

#### StatefulSet
Like a Deployment, but for stateful services (databases). Each pod gets
a stable hostname (`pod-0`, `pod-1`) and its own dedicated
PersistentVolumeClaim. The Bitnami PostgreSQL subchart manages a
StatefulSet for you, so you don't write this yourself.

#### PersistentVolumeClaim (PVC)
The equivalent of a named volume in docker compose. The cluster provisions
a real disk (cloud block storage, NFS, etc.) and mounts it into the pod.
Data survives pod restarts and rescheduling to other nodes.

#### Namespace
A logical partition inside a cluster. Think of it like a separate Docker
Compose project — it gives resources their own name scope and lets you
apply access controls. This chart deploys into a `mergician` namespace.

---

### What Helm adds on top of plain Kubernetes YAML

Raw Kubernetes YAML is repetitive and hard to configure for different
environments. Helm solves this with **templates** (Go template syntax in
`.yaml` files) and a **values file**.

```
values.yaml          ← your configuration (image tag, URLs, replica count)
     ↓
templates/*.yaml     ← YAML with {{ placeholders }}
     ↓
helm template        ← renders final Kubernetes YAML
     ↓
kubectl apply        ← submits it to the cluster (helm install does both)
```

The `values.yaml` in this chart is the direct analogue of a docker compose
file with environment variables filled in. Everything you would have put in
a `.env` file goes into either `values.yaml` (for non-sensitive config) or
a Kubernetes Secret (for credentials).

A **release** is Helm's term for a named deployment of a chart. Running
`helm install mergician k8s/` creates a release called `mergician` that
Helm tracks. `helm upgrade`, `helm rollback`, and `helm uninstall` all
operate on this release name.

---

### How this chart maps to the docker-compose file

The `mergician-compose.yaml` file in the repo root is the development
compose file. Here is how each part maps to the Helm chart:

| `mergician-compose.yaml` | This Helm chart |
|---|---|
| `services.postgres` | Bitnami PostgreSQL subchart (StatefulSet + Service + PVC) |
| `services.mergician` | `templates/deployment.yaml` + `templates/service.yaml` |
| `ports: "5000:5000"` | `templates/ingress.yaml` (handles TLS too) |
| `environment: Mergician__*` | `templates/configmap.yaml` (plain) + `templates/secret.yaml` (sensitive) |
| `volumes: pgdata` | PostgreSQL PVC (managed by Bitnami subchart) |
| `healthcheck: curl /api/health` | `readinessProbe` + `startupProbe` in the Deployment |

---

### Deploying for the first time (quick walkthrough)

Assuming you have `kubectl` configured against your cluster and `helm` installed:

```bash
# 1. Download the PostgreSQL subchart
helm dependency update k8s/

# 2. Create the namespace
kubectl create namespace mergician

# 3. Create your credentials Secret (see Secret Management section above)
#    Example using kubectl directly (dev only):
kubectl create secret generic mergician-credentials \
  --namespace mergician \
  --from-literal=gitlab-service-token='glpat-xxxx' \
  --from-literal=gitlab-oauth-client-id='your-client-id' \
  --from-literal=gitlab-oauth-client-secret='your-client-secret'

kubectl create secret generic mergician-db-credentials \
  --namespace mergician \
  --from-literal=password='your-db-password'

# 4. Install the chart, pointing at your existing secrets
helm install mergician k8s/ \
  --namespace mergician \
  --set image.repository=your-registry.example.com/mergician \
  --set image.tag=1.0.0 \
  --set config.baseUrl=https://mergician.example.com \
  --set config.gitlab.url=https://gitlab.example.com \
  --set secrets.existingSecret=mergician-credentials \
  --set postgresql.auth.existingSecret=mergician-db-credentials

# 5. Watch the pods start up
kubectl get pods -n mergician -w

# 6. Check the application is healthy
kubectl exec -n mergician deploy/mergician -- curl -sf http://localhost:5000/api/health
```

That's it. From here, `helm upgrade` replaces `docker compose up --pull` for
applying changes, and `kubectl logs` / `kubectl describe pod` replace
`docker compose logs` / `docker inspect` for debugging.

---

### ArgoCD: GitOps automation

In a GitOps workflow with ArgoCD, you commit changes to this repository
and ArgoCD automatically detects the diff and re-runs the equivalent of
`helm upgrade` for you. The `argocd-app.yaml` file in this directory is
the ArgoCD Application manifest — it tells ArgoCD where the chart lives
(this repo) and what values to use. Once applied, every merged commit to
`main` that touches the `k8s/` directory will automatically roll out to
the cluster.
