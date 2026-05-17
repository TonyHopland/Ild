-- Create dedicated roles for each database
CREATE ROLE ild_core WITH LOGIN PASSWORD 'ild_core_password';
CREATE ROLE ild_workitems WITH LOGIN PASSWORD 'ild_workitems_password';

-- Create databases
CREATE DATABASE "IldCore";
CREATE DATABASE "IldWorkitems";

-- Grant connect privileges
GRANT CONNECT ON DATABASE "IldCore" TO ild_core;
GRANT CONNECT ON DATABASE "IldWorkitems" TO ild_workitems;

-- Grant schema privileges in each database
\c "IldCore"
GRANT ALL ON SCHEMA public TO ild_core;

\c "IldWorkitems"
GRANT ALL ON SCHEMA public TO ild_workitems;
