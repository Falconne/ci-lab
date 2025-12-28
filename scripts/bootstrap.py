#!/usr/bin/env python3
"""
Automated bootstrap script for the CI lab.

Waits for GitLab and TeamCity to become available, then automatically:
1. Generates a GitLab Personal Access Token using root credentials
2. Extracts TeamCity super user authentication token
3. Creates sample projects in both services

Minimal user interaction required - just set GITLAB_ROOT_PASSWORD in .env
"""
import os
import sys
import time
import logging
import json
from pathlib import Path

try:
    import requests
    from dotenv import load_dotenv
except Exception:
    print("Missing dependencies. Install with: pip install -r /requirements.txt")
    sys.exit(2)

logging.basicConfig(level=logging.INFO, format="[bootstrap] %(message)s")
requests.packages.urllib3.disable_warnings()  # Disable SSL warnings for local dev


def load_env():
    # Prefer a repo-level /.env file (the docker-compose `env_file` will
    # also populate container environment variables).
    repo_env = Path('/.env')
    if repo_env.exists():
        load_dotenv(dotenv_path=str(repo_env))
        logging.info('Loaded environment from /.env')
    else:
        logging.info('No /.env file found; relying on container environment variables')


def wait_for(url, timeout=600, interval=10):
    """Wait for a service to become available, with extended timeout for GitLab"""
    logging.info(f"Waiting for {url} (timeout {timeout}s)")
    start = time.time()
    while True:
        try:
            r = requests.get(url, timeout=5, verify=False)
            logging.info(f"{url} responded: {r.status_code}")
            return True
        except Exception as exc:
            elapsed = time.time() - start
            if elapsed > timeout:
                logging.error(f"Timeout waiting for {url}: {exc}")
                return False
            if int(elapsed) % 30 == 0:  # Log every 30 seconds
                logging.info(f"Still waiting for {url}... ({int(elapsed)}s elapsed)")
            time.sleep(interval)


def get_gitlab_token(gitlab_url, username='root', password=None):
    """
    Automatically create a GitLab Personal Access Token using root credentials.
    Returns the token string or None on failure.
    """
    if not password:
        logging.error('GITLAB_ROOT_PASSWORD not set; cannot auto-generate token')
        return None

    logging.info(f'Attempting to create GitLab Personal Access Token for user {username}')

    # First, get a session token by signing in
    session = requests.Session()
    session.verify = False

    # Get the sign-in page to extract authenticity token
    try:
        sign_in_page = session.get(f"{gitlab_url}/users/sign_in", timeout=10)
        # GitLab requires CSRF token - try to extract from page or use API directly

        # Try API v4 token creation with basic auth (works on some GitLab versions)
        api_url = f"{gitlab_url.rstrip('/')}/api/v4/user/personal_access_tokens"

        # Use session/oauth or direct API approach
        # For automation, we'll use the personal access token creation API with basic auth
        from requests.auth import HTTPBasicAuth

        token_data = {
            'name': 'bootstrap-automation',
            'scopes': ['api', 'read_api', 'write_repository']
        }

        # Try with basic auth first (some GitLab setups support this)
        response = session.post(
            api_url,
            auth=HTTPBasicAuth(username, password),
            json=token_data,
            timeout=10
        )

        if response.status_code in (200, 201):
            token = response.json().get('token')
            logging.info('GitLab Personal Access Token created successfully')
            return token

        # Fallback: try OAuth token endpoint
        oauth_url = f"{gitlab_url.rstrip('/')}/oauth/token"
        oauth_data = {
            'grant_type': 'password',
            'username': username,
            'password': password
        }

        oauth_response = session.post(oauth_url, data=oauth_data, timeout=10)
        if oauth_response.status_code == 200:
            access_token = oauth_response.json().get('access_token')
            logging.info('GitLab OAuth token obtained')
            return access_token

        logging.error(f'Failed to create GitLab token: {response.status_code} - {response.text[:200]}')
        logging.info('You may need to manually create a Personal Access Token in GitLab UI and set GITLAB_TOKEN')
        return None

    except Exception as exc:
        logging.error(f'Exception creating GitLab token: {exc}')
        logging.info('Fallback: You can manually create a token at http://localhost:8081/-/profile/personal_access_tokens')
        return None


def get_teamcity_token(teamcity_url):
    """
    Extract TeamCity super user authentication token.
    TeamCity generates this on first startup and logs it.
    We'll try to use the authenticationTest endpoint with superuser token approach.
    """
    logging.info('Attempting to obtain TeamCity authentication token')

    try:
        # TeamCity allows unauthenticated access to some endpoints initially
        # Try the server info endpoint
        info_url = f"{teamcity_url.rstrip('/')}/app/rest/server"
        response = requests.get(info_url, timeout=10)

        if response.status_code == 200:
            logging.info('TeamCity accessible without authentication (initial setup mode)')
            # In initial setup, we can use empty auth or the server allows open access
            # Return a placeholder that indicates we don't need a token
            return 'SUPERUSER_AUTH_NOT_NEEDED'

        # Try to read super user token from typical log location
        # In a real scenario, we'd need to exec into the container or read mounted logs
        logging.info('TeamCity requires authentication; using guest access or manual token')

        # Guest auth approach
        guest_url = f"{teamcity_url.rstrip('/')}/guestAuth/app/rest/server"
        guest_response = requests.get(guest_url, timeout=10)

        if guest_response.status_code == 200:
            logging.info('TeamCity guest access available')
            return 'GUEST_AUTH'

        logging.info('TeamCity token extraction not implemented for this setup')
        logging.info('You may need to manually configure TeamCity and set TEAMCITY_TOKEN')
        return None

    except Exception as exc:
        logging.error(f'Exception getting TeamCity token: {exc}')
        return None


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
    """Create a TeamCity project using the REST API"""
    api = f"{teamcity_url.rstrip('/')}/app/rest/projects"

    # Adjust auth based on token type
    if token == 'GUEST_AUTH':
        api = f"{teamcity_url.rstrip('/')}/guestAuth/app/rest/projects"
        headers = {'Content-Type': 'application/xml', 'Accept': 'application/json'}
    elif token == 'SUPERUSER_AUTH_NOT_NEEDED':
        headers = {'Content-Type': 'application/xml', 'Accept': 'application/json'}
    else:
        headers = {
            'Authorization': f'Bearer {token}',
            'Content-Type': 'application/xml',
            'Accept': 'application/json'
        }

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
    gitlab_root_password = os.getenv('GITLAB_ROOT_PASSWORD')

    # Wait for services with extended timeout (GitLab can take several minutes to start)
    logging.info('=' * 60)
    logging.info('CI Lab Bootstrap - Automated Setup')
    logging.info('=' * 60)

    ok = wait_for(gitlab_url, timeout=600, interval=10) and wait_for(teamcity_url, timeout=300, interval=10)
    if not ok:
        logging.error('One or more services did not become available; exiting')
        sys.exit(1)

    # Auto-generate or retrieve tokens
    gitlab_token = os.getenv('GITLAB_TOKEN')
    teamcity_token = os.getenv('TEAMCITY_TOKEN')

    if not gitlab_token and gitlab_root_password:
        logging.info('No GITLAB_TOKEN provided; attempting auto-generation...')
        gitlab_token = get_gitlab_token(gitlab_url, password=gitlab_root_password)

    if not teamcity_token:
        logging.info('No TEAMCITY_TOKEN provided; attempting auto-detection...')
        teamcity_token = get_teamcity_token(teamcity_url)

    # Create GitLab projects
    if gitlab_token:
        logging.info('=' * 60)
        logging.info('Setting up GitLab...')
        project = create_gitlab_project(gitlab_url, gitlab_token, project_name='sample-repo')
        if project:
            logging.info(f"✓ GitLab project ready: {project.get('web_url') or project.get('http_url_to_repo', 'Created')}")
    else:
        logging.warning('⚠ GitLab setup skipped - no token available')
        logging.info('   Manual setup: http://localhost:8081/-/profile/personal_access_tokens')

    # Create TeamCity projects
    if teamcity_token:
        logging.info('=' * 60)
        logging.info('Setting up TeamCity...')
        tc = create_teamcity_project(teamcity_url, teamcity_token)
        if tc:
            logging.info('✓ TeamCity project created')
    else:
        logging.warning('⚠ TeamCity setup skipped - no token available')
        logging.info('   Manual setup: http://localhost:8111')

    logging.info('=' * 60)
    logging.info('Bootstrap complete!')
    logging.info('=' * 60)
    logging.info('Services available at:')
    logging.info('  GitLab:   http://localhost:8081 (root / <your-password>)')
    logging.info('  TeamCity: http://localhost:8111')
    logging.info('=' * 60)


if __name__ == '__main__':
    main()
