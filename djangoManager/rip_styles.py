from bs4 import BeautifulSoup
import requests, time


headers = {
   'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:131.0) Gecko/20100101 Firefox/131.0',
   'Accept': '*/*', 
   'Accept-Language': 'en-US,en;q=0.5',
   'Accept-Encoding': 'gzip, deflate, br, zstd',
   'Referer': 'https://midlibrary.io/styles?67f18bd1_page=30',
   'DNT': '1',
   'Sec-GPC': '1',
   'Connection': 'keep-alive',
   'Cookie': 'XXXXXXXXXXXXXXXXXXXXXXXXXXXXX',
   'Sec-Fetch-Dest': 'empty',
   'Sec-Fetch-Mode': 'cors', 
   'Sec-Fetch-Site': 'same-origin',
   'Priority': 'u=4',
   'TE': 'trailers'
}


def download_page(n):
    url='https://midlibrary.io/styles?67f18bd1_page='+str(n)
    print(url)
    response = requests.get(url, headers=headers)
    return response.text


def extract_artist_names(html_content):
    soup = BeautifulSoup(html_content, 'html.parser')
    
    # Find all elements with class 'copy3' that contain artist names
    artist_elements = soup.find_all('div', class_='copy3')
    
    # Extract and clean artist names
    artists = []
    for element in artist_elements:
        name = element.text.strip()
        if name:  # Only add non-empty names
            artists.append(name)
    
    return artists

def process(content, allartists):
    
    artists = extract_artist_names(content)
    for e in artists:
        allartists.add(e)
    
   
import ipdb;ipdb.set_trace()
allartists=set()
for n in range(1,100):
    time.sleep(3)
    txt=download_page(n)
    process(txt, allartists)
    for artist in sorted(allartists):
        print(artist)
    print(n)
    print(len(allartists))