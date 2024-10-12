import logging, json, traceback, time
from django.conf import settings
from urllib.parse import urlencode
from django.db import connection
from django.db.backends.utils import CursorWrapper
from pprint import pformat

logger = logging.getLogger(__name__)

class QueryLoggerCursorWrapper(CursorWrapper):
    def __init__(self, cursor, db, query_logger):
        self.cursor = cursor
        self.db = db
        self.query_logger = query_logger

    def execute(self, sql, params=None):
        start = time.time()
        try:
            return self.cursor.execute(sql, params)
        finally:
            duration = time.time() - start
            self.query_logger.log_query(sql, params, duration)

    def executemany(self, sql, param_list):
        start = time.time()
        try:
            return self.cursor.executemany(sql, param_list)
        finally:
            duration = time.time() - start
            self.query_logger.log_query(sql, param_list, duration)

class QueryLogger:
    def __init__(self):
        self.queries = []

    def log_query(self, sql, params, duration):
        self.queries.append({
            'sql': self.format_sql(sql, params),
            'time': f"{duration:.3f}"
        })

    def format_sql(self, sql, params):
        if params:
            return sql % tuple(map(repr, params))
        return sql

class RequestLoggingMiddleware:
    def __init__(self, get_response):
        self.get_response = get_response

    def __call__(self, request):
        query_logger = QueryLogger()
        original_cursor = connection.cursor

        def cursor():
            return QueryLoggerCursorWrapper(original_cursor(), connection, query_logger)

        connection.cursor = cursor
        
        start_time = time.time()
        response = self.get_response(request)
        duration = time.time() - start_time

        connection.cursor = original_cursor
        
        bads=['/favicon.ico', '/robots.txt', '/jsi18n/']
        if not any(bad in request.path for bad in bads) and settings.DEBUG:
            log_data = {
                'method': request.method,
                'full_path': self.get_full_path(request),
                'status_code': response.status_code,
                'ip': self.get_client_ip(request),
                'duration': f"{duration:.3f}s",
                'queries': query_logger.queries
            }
            log_message = self.format_log_message(log_data)
            logger.info(log_message)
            # Add detailed logging
            # logger.info(f"Request: {(request.POST)}")
            # logger.info(f"Response: {(response.content)}")
            for q in query_logger.queries:
                logger.info(f"\t{q['sql']} (Time: {q['time']}s)")

        # Remove this line as it's causing the debugger to pause execution
        # import ipdb;ipdb.set_trace()

        if settings.LOCAL and request.method == 'POST' and '/terrainparkour/' not in request.path and '/login/?next' not in request.path and request.path.startswith('/terrain/'):
            try:
                content = response.content.decode('utf-8')
                loadedThing = json.loads(content)
                if 'res' not in loadedThing:
                    logger.info(f"no res in {loadedThing} (perhaps okay)")
                else:
                    item = loadedThing['res']
                    formatted_item = pformat(item, indent=4, width=100)
                    # logger.info(f"\nResponse content:\n{formatted_item}")
            except json.JSONDecodeError:
                logger.info(f"Unable to decode JSON response: {content}")
            except Exception as e:
                logger.info(f"Error processing response: {str(e)}")

        return response

    def get_client_ip(self, request):
        x_forwarded_for = request.META.get('HTTP_X_FORWARDED_FOR')
        return x_forwarded_for.split(',')[0] if x_forwarded_for else request.META.get('REMOTE_ADDR')

    def get_full_path(self, request):
        full_path = request.get_full_path() + " "
        remoteActionName = ""
        values=[]
        if request.method in ['POST', ]:
            full_path='postEndpoint\t'
            el = next(request.POST.items())
            try:
                vv=json.loads(el[0])
                for key, value in sorted(vv.items()):
                    if key=='remoteActionName':
                        remoteActionName=value
                        if remoteActionName=='userData':
                            break
                    if key=='data':
                        if type(value)==list:
                            continue
                        for k,v in sorted(value.items()):
                            values.append((k,v))
                        continue
                    if key=='secret': 
                        continue
            except:
                pass
            
        combo = ' '.join([f"{el[0]}={el[1]}" for el in values])
        return f"{full_path:10}{remoteActionName:30}{combo}"

    def format_log_message(self, log_data):
        base_message = f"{log_data['method']:20}\t{log_data['status_code']}\t{log_data['duration']}\t{log_data['full_path']}"
        return f"{base_message}\nIP: {log_data['ip']}\nQueries: {len(log_data['queries'])}"