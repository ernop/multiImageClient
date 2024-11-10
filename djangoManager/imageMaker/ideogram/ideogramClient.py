import aiohttp
import json, datetime
from typing import Dict, Any, List, Optional
from enum import Enum, auto
import asyncio
from dataclasses import dataclass, asdict


class IdeogramAspectRatio(Enum):
    ASPECT_1_1 = "1:1"
    ASPECT_16_9 = "16:9"
    ASPECT_9_16 = "9:16"
    ASPECT_4_3 = "4:3"
    ASPECT_3_4 = "3:4"

class IdeogramModel(Enum):
    V_1 = "1"
    V_2 = "2"
 
class IdeogramMagicPromptOption(Enum):
    ON = "on"
    OFF = "off"

class IdeogramStyleType(Enum):
    GENERAL = "general"
    ANIME = "anime"
    PHOTOGRAPHY = "photography"

@dataclass
class IdeogramDetails:
    aspect_ratio: Optional[IdeogramAspectRatio] = None
    model: Optional[IdeogramModel] = None
    magic_prompt_option: Optional[IdeogramMagicPromptOption] = None
    style_type: Optional[IdeogramStyleType] = None
    negative_prompt: str = ""

@dataclass
class IdeogramGenerateRequest:
    prompt: str
    aspect_ratio: Optional[IdeogramAspectRatio] = None
    resolution: Optional[str] = None
    model: Optional[IdeogramModel] = None
    magic_prompt_option: Optional[IdeogramMagicPromptOption] = None
    style_type: Optional[IdeogramStyleType] = None
    negative_prompt: str = ""
    seed: Optional[int] = None

    def to_dict(self):
        return {k: v.value if isinstance(v, Enum) else v for k, v in asdict(self).items() if v is not None}

@dataclass
class ImageObject:
    url: str
    prompt: str
    resolution: str
    is_image_safe: bool
    seed: int

@dataclass
class GenerateResponse:
    created: datetime
    data: List[ImageObject]


class IdeogramClient:
    BASE_URL = "https://api.ideogram.ai"

    def __init__(self, api_key: str):
        self.api_key = api_key
        self._session: Optional[aiohttp.ClientSession] = None

    async def _get_session(self) -> aiohttp.ClientSession:
        if self._session is None or self._session.closed:
            self._session = aiohttp.ClientSession(headers={"Api-Key": self.api_key})
        return self._session

    async def generate_image_async(self, request: IdeogramGenerateRequest) -> GenerateResponse:
        session = await self._get_session()
        json_request = json.dumps({"image_request": request.to_dict()}, default=str)
        
        async with session.post(f"{self.BASE_URL}/generate", data=json_request, headers={"Content-Type": "application/json"}) as response:
            if response.status != 200:
                error_content = await response.text()
                raise aiohttp.ClientResponseError(
                    response.request_info,
                    response.history,
                    status=response.status,
                    message=f"API request failed with status code {response.status}. Response: {error_content}"
                )
            
            content = await response.json()
            return GenerateResponse(
                created=datetime.fromisoformat(content["created"]),
                data=[ImageObject(**img) for img in content["data"]]
            )

    async def close(self):
        if self._session and not self._session.closed:
            await self._session.close()

class IdeogramService:
    def __init__(self, api_key: str, max_concurrency: int):
        self.ideogram_client = IdeogramClient(api_key)
        self.semaphore = asyncio.Semaphore(max_concurrency)

    async def process_prompt_async(self, prompt_details: Dict[str, Any], stats: Dict[str, Any]) -> Dict[str, Any]:
        async with self.semaphore:
            try:
                ideogram_details = prompt_details.get("ideogram_details", {})
                request = IdeogramGenerateRequest(
                    prompt=prompt_details["prompt"],
                    **ideogram_details
                )

                stats["ideogram_request_count"] += 1
                response = await self.ideogram_client.generate_image_async(request)

                if response.data and len(response.data) == 1:
                    image_object = response.data[0]
                    returned_prompt = image_object.prompt
                    
                    if returned_prompt != prompt_details["prompt"]:
                        prompt_details["prompt"] = returned_prompt
                        prompt_details["prompt_rewrite"] = {
                            "source": "Ideogram rewrite",
                            "rewrite": returned_prompt
                        }

                    return {
                        "response": response,
                        "is_success": True,
                        "url": image_object.url,
                        "prompt_details": prompt_details,
                        "generator": "Ideogram"
                    }
                elif response.data and len(response.data) > 1:
                    raise Exception("Multiple images returned? I can't handle this! Who knew!")
                else:
                    return {
                        "is_success": False,
                        "error_message": "No images generated",
                        "prompt_details": prompt_details,
                        "generator": "Ideogram"
                    }
            except Exception as ex:
                return {
                    "is_success": False,
                    "error_message": str(ex),
                    "prompt_details": prompt_details,
                    "generator": "Ideogram"
                }

    # If you still need a synchronous version, you can add this method:
    def process_prompt(self, prompt_details: Dict[str, Any], stats: Dict[str, Any]) -> Dict[str, Any]:
        return asyncio.run(self.process_prompt_async(prompt_details, stats))
