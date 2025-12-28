#!/usr/bin/env python3
"""
Simple bootstrap script for the CI lab.

It waits for GitLab and TeamCity HTTP endpoints to become reachable, then
performs minimal initialisation steps if API tokens are provided via
environment variables. This is a scaffold — adapt the API calls to your
organisation's needs.
"""
import os
import sys
import time
import logging
from pathlib import Path

try:
    import requests
    from dotenv import load_dotenv
except Exception:
    print("Missing dependencies. Install with: pip install -r /scripts/requirements.txt")
    sys.exit(2)

logging.basicConfig(level=logging.INFO, format="[bootstrap] %(message)s")


def load_env():
    # Prefer a repo-level /.env file (the docker-compose `env_file` will
    # also populate container environment variables).
    repo_env = Path('/.env')
    if repo_env.exists():
        load_dotenv(dotenv_path=str(repo_env))
        logging.info('Loaded environment from /.env')
    else:
        logging.info('No /.env file found; relying on container environment variables')


def wait_for(url, timeout=300, interval=5):
    logging.info(f"Waiting for {url} (timeout {timeout}s)")
    start = time.time()
    while True:
        try:
            r = requests.get(url, timeout=5)
            logging.info(f"{url} responded: {r.status_code}")
            return True
        except Exception as exc:
            if time.time() - start > timeout:
                logging.error(f"Timeout waiting for {url}: {exc}")
                return False
            time.sleep(interval)


def create_gitlab_project(gitlab_url, token, project_name='sample-repo'):
    api = f"{gitlab_url.rstrip('/')}/api/v4/projects"
    headers = {'PRIVATE-TOKEN': token}
    data = {'name': project_name, 'initialize_with_readme': True}
    logging.info(f"Creating GitLab project '{project_name}' via {api}")
    try:
        r = requests.post(api, headers=headers, data=data, timeout=10)
        if r.status_code in (201, 200):
            logging.info('GitLab project created successfully')
            return r.json()
        elif r.status_code == 409:
            logging.info('GitLab project already exists')
            return r.json()
        else:
            logging.error(f"GitLab API error {r.status_code}: {r.text}")
            return None
    except Exception as exc:
        logging.error(f"Failed to call GitLab API: {exc}")
        return None


def create_teamcity_project(teamcity_url, token, project_name='Sample Project', project_id='SampleProject'):
    # TeamCity's REST API supports creating a project via POST /app/rest/projects
    api = f"{teamcity_url.rstrip('/')}/app/rest/projects"
    headers = {
        'Authorization': f'Bearer {token}',
        'Content-Type': 'application/xml',
        'Accept': 'application/json'
    }
    # Minimal XML for new project; TeamCity may require parentProject - adjust as needed
    xml = f'<newProjectDescription name="{project_name}" id="{project_id}" />'
    logging.info(f"Creating TeamCity project '{project_name}' via {api}")
    try:
        r = requests.post(api, headers=headers, data=xml.encode('utf-8'), timeout=10)
        if r.status_code in (200, 201):
            logging.info('TeamCity project created successfully')
            return r.json() if r.text else {'status': 'created'}
        elif r.status_code == 409:
            logging.info('TeamCity project already exists')
            return r.json() if r.text else {'status': 'exists'}
        else:
            logging.error(f"TeamCity API error {r.status_code}: {r.text}")
            return None
    except Exception as exc:
        logging.error(f"Failed to call TeamCity API: {exc}")
        return None


def main():
    load_env()

    gitlab_url = os.getenv('GITLAB_URL', 'http://gitlab')
    teamcity_url = os.getenv('TEAMCITY_URL', 'http://teamcity:8111')

    ok = wait_for(gitlab_url) and wait_for(teamcity_url)
    if not ok:
        logging.error('One or more services did not become available; exiting')
        sys.exit(1)

    gitlab_token = os.getenv('GITLAB_TOKEN')
    teamcity_token = os.getenv('TEAMCITY_TOKEN')

    if gitlab_token:
        project = create_gitlab_project(gitlab_url, gitlab_token, project_name='sample-repo')
        if project:
            logging.info(f"GitLab project ready: {project.get('web_url') or project.get('http_url_to_repo')}")
    else:
        logging.info('No GITLAB_TOKEN provided — skipping automated GitLab setup.')

    if teamcity_token:
        tc = create_teamcity_project(teamcity_url, teamcity_token)
        if tc:
            logging.info('TeamCity project creation attempted; review response.')
    else:
        logging.info('No TEAMCITY_TOKEN provided — skipping automated TeamCity setup.')

    logging.info('Bootstrap complete. Review this script to implement your organisation-specific steps.')


if __name__ == '__main__':
    main()
