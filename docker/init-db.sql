-- Create business database for factories/workshops/devices metadata
CREATE DATABASE amgateway_business;

-- Create dedicated user for business database
CREATE USER sa WITH PASSWORD 'sa';
GRANT ALL PRIVILEGES ON DATABASE amgateway_business TO sa;

-- Grant schema-level permissions inside business database
\c amgateway_business
GRANT ALL ON SCHEMA public TO sa;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO sa;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO sa;
