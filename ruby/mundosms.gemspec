# frozen_string_literal: true

# La gema del SDK de Ruby: un solo fichero (lib/mundosms.rb), sin dependencias. La versión es la de MundoSMS::VERSION, leída
# del fichero sin cargarlo. Para construirla: gem build mundosms.gemspec
version = File.read(File.join(__dir__, "lib", "mundosms.rb"))[/^  VERSION = "([0-9.]+)"$/, 1]

Gem::Specification.new do |s|
  s.name = "mundosms"
  s.version = version
  s.summary = "Cliente de la API REST v3 de MundoSMS: SMS, WhatsApp, centralita, VozPush y NumCheck."
  s.description = "Firma cada petición, pone una Idempotency-Key en cada POST, reintenta solo lo que es seguro repetir, " \
                  "recorre los listados por su cursor y comprueba la firma de los avisos. Un solo fichero, sin dependencias."
  s.authors = ["MundoSMS"]
  s.email = ["contacto@mundosms.es"]
  s.homepage = "https://www.mundosms.es/api-v3"
  s.license = "MIT"
  s.required_ruby_version = ">= 3.1"
  s.files = ["lib/mundosms.rb", "LICENSE", "README.md"]
  s.require_paths = ["lib"]
  s.metadata = {
    "documentation_uri" => "https://www.mundosms.es/api-v3",
    "source_code_uri" => "https://github.com/mundosms/mundosms-sdk",
    "changelog_uri" => "https://github.com/mundosms/mundosms-sdk/blob/main/CHANGELOG.md",
    "bug_tracker_uri" => "https://github.com/mundosms/mundosms-sdk/issues"
  }
end
