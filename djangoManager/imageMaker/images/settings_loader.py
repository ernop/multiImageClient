import json
import os
from typing import Dict, Any

class SettingsLoader:
    def __init__(self, settings_dict: Dict[str, Any]):
        self.image_download_base_folder = settings_dict.get('ImageDownloadBaseFolder', '')
        self.save_json_log = settings_dict.get('SaveJsonLog', False)
        self.enable_logging = settings_dict.get('EnableLogging', False)
        self.annotation_side = settings_dict.get('AnnotationSide', '')
        self.bfl_api_key = settings_dict.get('BFLApiKey', '')
        self.ideogram_api_key = settings_dict.get('IdeogramApiKey', '')
        self.openai_api_key = settings_dict.get('OpenAIApiKey', '')
        self.anthropic_api_key = settings_dict.get('AnthropicApiKey', '')

    @classmethod
    @classmethod
    def load_from_file(cls, file_path: str) -> 'SettingsLoader':
        if not os.path.exists(file_path):
            print(os.getcwd())
            raise FileNotFoundError(f"Settings file not found: {file_path}")

        with open(file_path, 'r', encoding='utf-8-sig') as f:
            settings_dict = json.load(f)

        return cls(settings_dict)

    def __str__(self):
        return (
            f"Current settings:\n"
            f"Image Download Base:\t{self.image_download_base_folder}\n"
            f"Save JSON Log:\t{self.save_json_log}\n"
            f"Enable Logging:\t\t{self.enable_logging}\n"
            f"Annotation Side:\t{self.annotation_side}"
        )

def load_settings(settings_file_path: str = '../../MultiImageClient/settings.json') -> SettingsLoader:
    try:
        settings = SettingsLoader.load_from_file(settings_file_path)
        print(settings)
        return settings
    except Exception as e:
        print(f"Error loading settings: {e}")
        return None

