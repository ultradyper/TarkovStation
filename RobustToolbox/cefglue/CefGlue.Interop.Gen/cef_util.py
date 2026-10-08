from pcpp import Preprocessor

class CustomPreprocessor(Preprocessor):
    def __init__(self, lexer=None):
        super().__init__(lexer)

        self.pass_through_comments = True

    def on_comment(self, tok): # type: ignore
        if self.pass_through_comments:
            return True

        return super().on_comment(tok)

    def on_directive_handle(self, directive, toks, ifpassthru, precedingtoks):
        return super().on_directive_handle(directive, toks, ifpassthru, precedingtoks)

def eval_macro(preproc: Preprocessor, name: str) -> str:
    macro = preproc.macros[name]
    evaluated = preproc.expand_macros(macro.value)
    return "".join((tok.value for tok in evaluated))

def create_preproc_common(header_dir: str, api_version: int) -> CustomPreprocessor:
    preprocessor = CustomPreprocessor()
    preprocessor.define(f"CEF_API_VERSION {api_version}")
    preprocessor.add_path(header_dir + "/..")

    return preprocessor

def create_preproc_windows(header_dir: str, api_version: int) -> CustomPreprocessor:
    preprocessor = create_preproc_common(header_dir, api_version)
    preprocessor.define("_WIN32")
    preprocessor.define("_MSC_VER")
    preprocessor.define("__x86_64__")

    return preprocessor

def create_preproc_linux(header_dir: str, api_version: int) -> CustomPreprocessor:
    preprocessor = create_preproc_common(header_dir, api_version)
    preprocessor.define("__linux__")
    preprocessor.define("__GNUC__")
    preprocessor.define("__x86_64__")
    preprocessor.define("__WCHAR_MAX__ 0xffffffff")

    return preprocessor

def create_preproc_macos(header_dir: str, api_version: int) -> CustomPreprocessor:
    preprocessor = create_preproc_common(header_dir, api_version)
    preprocessor.define("__APPLE__")
    preprocessor.define("__GNUC__")
    preprocessor.define("__aarch64__")
    preprocessor.define("__WCHAR_MAX__ 0xffffffff")

    return preprocessor

def throw_tokens(preproc: Preprocessor):
    while preproc.token() is not None:
        pass
