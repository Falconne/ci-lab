-- Initialize TeamCity database and user
DO
$$
BEGIN
	IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'teamcity') THEN
		CREATE ROLE teamcity WITH LOGIN ENCRYPTED PASSWORD 'teamcity_password';
	END IF;
END
$$;

DO
$$
BEGIN
	IF NOT EXISTS (SELECT FROM pg_database WHERE datname = 'teamcity') THEN
		CREATE DATABASE teamcity OWNER teamcity;
	END IF;
END
$$;
