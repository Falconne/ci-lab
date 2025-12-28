# Purpose
This repo contains tools for creating a mock CI/CD environment using Gitlab for source control and TeamCity for CI/CD. It also includes tools for creating dummy data in both services. The purpose is to be able to spin up test infrastructure in a docker based lab environment, so an organisation that uses Gitlab+TeamCity for CI/CD and have a lot of internally developed tools that interact with these servers via REST API, can test updates against a representative sandbox environment, instead of their production CI/CD.

# Directory Structure
The root of the repo contains a docker compose file and related settings for spinning up Gitlab and TeamCity, sharing the same network and exposing necessary UI and API ports to the host. The data for each service is internal to the container and not persisted to disk, so each time the services are recreated we will have a clean start for testing.

The compose file will execute the `scripts/bootstrap.py` script to upon creation of services to initialise them with data (see below)

## scripts
This subdirectory contains python scripts for for initialising Gitlab and TeamCity with a known set of repos and CIs.

## data
This subdirectory contains data files and sample repos to be used by the scripts for initialising data on the services.